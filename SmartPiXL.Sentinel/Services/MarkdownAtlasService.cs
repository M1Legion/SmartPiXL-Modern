using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// MARKDOWN ATLAS SERVICE — Reads docs/atlas/*.md files and serves them as the
// JSON structure expected by atlas.html. Replaces the SQL-backed Docs.Section
// approach with version-controlled markdown files as the source of truth.
//
// Each markdown file contains YAML frontmatter and 4 tier sections:
//   ## Atlas Public       → PitchHtml
//   ## Atlas Internal     → ManagementHtml
//   ## Atlas Technical    → TechnicalHtml
//   ## Atlas Private      → WalkthroughHtml
//
// Files are read from disk and cached. Cache invalidates when any file changes
// (FileSystemWatcher). Live SQL metrics are still fetched from Docs.Metric.
// ============================================================================

public sealed partial class MarkdownAtlasService
{
    private readonly string _docsRoot;
    private readonly ITrackingLogger _logger;
    private readonly MarkdownPipeline _pipeline;
    private readonly FileSystemWatcher? _watcher;

    private volatile IReadOnlyList<AtlasSection> _sections = [];
    private long _lastLoadedTicks = DateTime.MinValue.Ticks;

    // Tier section header regex — matches "## Atlas Public", "## Atlas Internal", etc.
    [GeneratedRegex(@"^##\s+Atlas\s+(Public|Internal|Technical|Private)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex TierHeaderRegex();

    // Frontmatter regex — YAML block between --- markers at file start
    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    // Mermaid code block regex — extract ```mermaid blocks from raw markdown
    [GeneratedRegex(@"```mermaid\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex MermaidBlockRegex();

    // Mermaid HTML block regex — fix Markdig output: <pre><code class="language-mermaid">...</code></pre>
    [GeneratedRegex(@"<pre><code class=""language-mermaid"">(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex MermaidHtmlBlockRegex();

    // Section catalog: slug → (category, sortOrder, iconClass, title override)
    // This maps the markdown file structure to the atlas.html card layout.
    private static readonly Dictionary<string, (string Category, int SortOrder, string Icon, string? TitleOverride)> SectionCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        // Architecture
        ["architecture/overview"]           = ("Architecture", 10, "globe", "SmartPiXL Overview"),
        ["architecture/data-flow"]          = ("Architecture", 11, "git-branch", "Data Flow"),
        ["architecture/edge"]               = ("Architecture", 12, "zap", "PiXL Edge"),
        ["architecture/forge"]              = ("Architecture", 13, "hammer", "SmartPiXL Forge"),
        ["architecture/sentinel"]           = ("Architecture", 14, "monitor", "SmartPiXL Sentinel"),

        // Subsystems
        ["subsystems/pixl-script"]          = ("Subsystems", 20, "code", "PiXL Script"),
        ["subsystems/fingerprinting"]       = ("Subsystems", 21, "fingerprint", "Fingerprinting"),
        ["subsystems/bot-detection"]        = ("Subsystems", 22, "shield", "Bot Detection"),
        ["subsystems/enrichment-pipeline"]  = ("Subsystems", 23, "layers", "Enrichment Pipeline"),
        ["subsystems/identity-resolution"]  = ("Subsystems", 24, "user-check", "Identity Resolution"),
        ["subsystems/etl"]                  = ("Subsystems", 25, "database", "ETL Pipeline"),
        ["subsystems/geo-intelligence"]     = ("Subsystems", 26, "map-pin", "Geo Intelligence"),
        ["subsystems/traffic-alerts"]       = ("Subsystems", 27, "bell", "Traffic Alerts"),
        ["subsystems/failover"]             = ("Subsystems", 28, "shield-check", "Failover & Durability"),

        // Database
        ["database/schema-map"]             = ("Database", 30, "table", "Schema Map"),
        ["database/etl-procedures"]         = ("Database", 31, "file-code", "ETL Procedures"),
        ["database/sql-features"]           = ("Database", 32, "cpu", "SQL 2025 Features"),

        // Operations
        ["operations/deployment"]           = ("Operations", 40, "rocket", "Deployment"),
        ["operations/troubleshooting"]      = ("Operations", 41, "wrench", "Troubleshooting"),
        ["operations/monitoring"]           = ("Operations", 42, "activity", "Monitoring"),
    };

    public MarkdownAtlasService(string docsRoot, ITrackingLogger logger)
    {
        _docsRoot = Path.GetFullPath(docsRoot);
        _logger = logger;

        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists()
            .UseEmphasisExtras()
            .Build();

        LoadAll();

        // Watch for file changes and reload
        if (Directory.Exists(_docsRoot))
        {
            _watcher = new FileSystemWatcher(_docsRoot, "*.md")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += (_, _) => InvalidateCache();
            _watcher.Created += (_, _) => InvalidateCache();
            _watcher.Deleted += (_, _) => InvalidateCache();
            _watcher.Renamed += (_, _) => InvalidateCache();
            _watcher.EnableRaisingEvents = true;
        }
    }

    public IReadOnlyList<AtlasSection> GetSections() => _sections;

    public AtlasSection? GetSectionBySlug(string slug)
        => _sections.FirstOrDefault(s => s.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<AtlasCategory> GetCategories()
    {
        return _sections
            .GroupBy(s => s.Category)
            .Select(g => new AtlasCategory(g.Key, g.OrderBy(s => s.SortOrder).ToList()))
            .OrderBy(c => c.Sections[0].SortOrder)
            .ToList();
    }

    private void InvalidateCache()
    {
        // Debounce — don't reload more than once per second
        var lastTicks = Interlocked.Read(ref _lastLoadedTicks);
        if ((DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc)).TotalSeconds < 1) return;

        try
        {
            LoadAll();
            _logger.Info($"[Atlas] Markdown cache reloaded — {_sections.Count} sections");
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Atlas] Markdown reload failed: {ex.Message}");
        }
    }

    private void LoadAll()
    {
        var sections = new List<AtlasSection>();
        int syntheticId = 1;

        if (!Directory.Exists(_docsRoot))
        {
            _logger.Warning($"[Atlas] Docs directory not found: {_docsRoot}");
            _sections = sections;
            return;
        }

        // Find all .md files except _index.md
        var files = Directory.GetFiles(_docsRoot, "*.md", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .OrderBy(f => f)
            .ToList();

        foreach (var filePath in files)
        {
            try
            {
                var section = ParseFile(filePath, syntheticId++);
                if (section is not null)
                    sections.Add(section);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Atlas] Failed to parse {filePath}: {ex.Message}");
            }
        }

        // Sort by catalog order
        sections.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));

        _sections = sections;
        Interlocked.Exchange(ref _lastLoadedTicks, DateTime.UtcNow.Ticks);
        _logger.Info($"[Atlas] Loaded {sections.Count} sections from {_docsRoot}");
    }

    private AtlasSection? ParseFile(string filePath, int id)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);

        // --- Relative path from docs root → slug key ---
        var relativePath = Path.GetRelativePath(_docsRoot, filePath)
            .Replace('\\', '/')
            .Replace(".md", "", StringComparison.OrdinalIgnoreCase);

        // Skip glossary — it's a reference doc, not a section card
        if (relativePath.Equals("glossary", StringComparison.OrdinalIgnoreCase))
            return null;

        // --- Frontmatter ---
        var frontmatter = ParseFrontmatter(content, out var body);
        var title = frontmatter.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(filePath);

        // --- Catalog lookup ---
        if (!SectionCatalog.TryGetValue(relativePath, out var catalogEntry))
        {
            // Unknown file — assign defaults
            catalogEntry = ("Other", 99, "file-text", null);
        }

        if (catalogEntry.TitleOverride is not null)
            title = catalogEntry.TitleOverride;

        // --- Extract Mermaid diagrams before markdown conversion ---
        string? mermaidDiagram = null;
        var mermaidMatch = MermaidBlockRegex().Match(body);
        if (mermaidMatch.Success)
        {
            mermaidDiagram = mermaidMatch.Groups[1].Value.Trim();
        }

        // --- Split by tier headers ---
        var tiers = SplitByTiers(body);

        // --- Convert each tier to HTML ---
        string? pitchHtml = ConvertTierToHtml(tiers.GetValueOrDefault("Public"));
        string? managementHtml = ConvertTierToHtml(tiers.GetValueOrDefault("Internal"));
        string? technicalHtml = ConvertTierToHtml(tiers.GetValueOrDefault("Technical"));
        string? walkthroughHtml = ConvertTierToHtml(tiers.GetValueOrDefault("Private"));

        // Build the slug (used for URL routing)
        var slug = relativePath.Replace("/", "-");

        return new AtlasSection
        {
            SectionId = id,
            Slug = slug,
            Title = title,
            Category = catalogEntry.Category,
            IconClass = catalogEntry.Icon,
            SortOrder = catalogEntry.SortOrder,
            PitchHtml = pitchHtml,
            ManagementHtml = managementHtml,
            TechnicalHtml = technicalHtml,
            WalkthroughHtml = walkthroughHtml,
            MermaidDiagram = mermaidDiagram,
            LastUpdated = File.GetLastWriteTimeUtc(filePath),
            UpdatedBy = "markdown",
            RelatedSlugs = frontmatter.GetValueOrDefault("related")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            Status = frontmatter.GetValueOrDefault("status") ?? "current"
        };
    }

    private Dictionary<string, string> ParseFrontmatter(string content, out string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var match = FrontmatterRegex().Match(content);

        if (!match.Success)
        {
            body = content;
            return result;
        }

        body = content[(match.Index + match.Length)..];
        var yaml = match.Groups[1].Value;

        // Simple YAML key: value parser (handles single-line values and lists)
        string? currentKey = null;
        var listValues = new List<string>();

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // List item (  - value)
            if (line.TrimStart().StartsWith("- ") && currentKey is not null)
            {
                listValues.Add(line.TrimStart()[2..].Trim());
                continue;
            }

            // Flush previous list
            if (currentKey is not null && listValues.Count > 0)
            {
                result[currentKey] = string.Join(",", listValues);
                listValues.Clear();
                currentKey = null;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();

                if (string.IsNullOrEmpty(value))
                {
                    // Could be a list — next lines will be "  - item"
                    currentKey = key;
                }
                else
                {
                    result[key] = value;
                }
            }
        }

        // Flush final list
        if (currentKey is not null && listValues.Count > 0)
        {
            result[currentKey] = string.Join(",", listValues);
        }

        return result;
    }

    private Dictionary<string, string> SplitByTiers(string body)
    {
        var tiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = TierHeaderRegex().Matches(body);

        if (matches.Count == 0) return tiers;

        for (int i = 0; i < matches.Count; i++)
        {
            var tierName = matches[i].Groups[1].Value; // "Public", "Internal", etc.
            var startIdx = matches[i].Index + matches[i].Length;
            var endIdx = (i + 1 < matches.Count) ? matches[i + 1].Index : body.Length;
            var tierContent = body[startIdx..endIdx].Trim();

            if (!string.IsNullOrWhiteSpace(tierContent))
                tiers[tierName] = tierContent;
        }

        return tiers;
    }

    private string? ConvertTierToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var html = Markdig.Markdown.ToHtml(markdown, _pipeline);

        // Fix Mermaid blocks: Markdig outputs <pre><code class="language-mermaid">...</code></pre>
        // Replace with <pre class="mermaid">...</pre> for client-side Mermaid.js rendering
        html = MermaidHtmlBlockRegex().Replace(html, "<pre class=\"mermaid\">$1</pre>");

        return html;
    }
}

// ============================================================================
// DTOs
// ============================================================================

public sealed class AtlasSection
{
    public int SectionId { get; init; }
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string IconClass { get; init; } = "file-text";
    public int SortOrder { get; init; }
    public string? PitchHtml { get; init; }
    public string? ManagementHtml { get; init; }
    public string? TechnicalHtml { get; init; }
    public string? WalkthroughHtml { get; init; }
    public string? MermaidDiagram { get; init; }
    public DateTime LastUpdated { get; init; }
    public string UpdatedBy { get; init; } = "markdown";
    public string[] RelatedSlugs { get; init; } = [];
    public string Status { get; init; } = "current";
}

public sealed class AtlasCategory
{
    public string Name { get; }
    public IReadOnlyList<AtlasSection> Sections { get; }

    public AtlasCategory(string name, IReadOnlyList<AtlasSection> sections)
    {
        Name = name;
        Sections = sections;
    }
}
