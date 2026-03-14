using SmartPiXL.SyntheticTraffic.Profiles;

namespace SmartPiXL.SyntheticTraffic.Generation;

// ============================================================================
// SESSION SIMULATOR — Groups page views into coherent visitor sessions.
//
// A real visitor doesn't fire a single isolated hit — they browse 2-8 pages
// over 30 seconds to 15 minutes. The same fingerprint, same IP, same device
// profile appears across all hits in a session. Timestamps increment
// monotonically with realistic dwell times between pages.
//
// This class produces a sequence of SyntheticHit records ready for HTTP dispatch.
// ============================================================================

/// <summary>
/// A single synthetic hit ready for HTTP dispatch.
/// </summary>
internal sealed class SyntheticHit
{
    public required string QueryString { get; init; }
    public required string UserAgent { get; init; }
    public required string Referrer { get; init; }
    public required string IpAddress { get; init; }
    public required int CompanyId { get; init; }
    public required int PixlId { get; init; }
    public required string Domain { get; init; }

    /// <summary>Request path: /{CompanyId}/{PixlId}_{Domain}_SMART.GIF</summary>
    public string RequestPath => $"/{CompanyId}/{PixlId}_{Domain}_SMART.GIF";
}

/// <summary>
/// Generates complete sessions of internally consistent synthetic hits.
/// </summary>
internal sealed class SessionSimulator
{
    private readonly TrafficSettings _settings;
    private readonly Network.IpGenerator _ipGen;
    private readonly Random _rng;

    // Session size distribution: power-law favoring shorter sessions
    // 1 page: 15%, 2 pages: 25%, 3-4 pages: 30%, 5-8 pages: 25%, 9+ pages: 5%
    private static readonly (int Min, int Max, int Weight)[] SessionSizeWeights =
    [
        (1, 1, 15), (2, 2, 25), (3, 4, 30), (5, 8, 25), (9, 15, 5),
    ];

    private static readonly int TotalSessionWeight =
        SessionSizeWeights.Sum(w => w.Weight);

    public SessionSimulator(TrafficSettings settings, Network.IpGenerator ipGen, Random rng)
    {
        _settings = settings;
        _ipGen = ipGen;
        _rng = rng;
    }

    /// <summary>
    /// Generate a complete session of hits for a random visitor.
    /// All hits share the same fingerprint, IP, device profile, and company.
    /// </summary>
    public SyntheticHit[] GenerateSession()
    {
        // Pick a random device profile
        var profile = ProfileCatalog.Select(_rng);

        // Pick company and pixel
        var companyIdx = _rng.Next(_settings.CompanyIds.Length);
        var companyId = _settings.CompanyIds[companyIdx];
        var pixlId = _settings.PixlIds[_rng.Next(_settings.PixlIds.Length)];

        // Generate IP for this visitor (stable across the session)
        var ip = _ipGen.Next(_rng);

        // Session size from weighted distribution
        var pageCount = PickSessionSize();

        // Session start: recent timestamp (within last hour)
        var now = DateTimeOffset.UtcNow;
        var sessionStartMs = now.ToUnixTimeMilliseconds() - _rng.Next(0, 3_600_000);

        // Build QS builder — fingerprints are stable for this session
        var qsBuilder = new QueryStringBuilder(profile, companyIdx, _rng);

        var hits = new SyntheticHit[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            var hitNumber = i + 1;
            var qs = qsBuilder.Build(_rng, companyId, hitNumber, sessionStartMs);
            var referrer = qsBuilder.GetReferrer(_rng, hitNumber);

            hits[i] = new SyntheticHit
            {
                QueryString = qs,
                UserAgent = profile.UserAgent,
                Referrer = referrer,
                IpAddress = ip,
                CompanyId = companyId,
                PixlId = pixlId,
                Domain = GetDomain(companyIdx),
            };
        }

        return hits;
    }

    private int PickSessionSize()
    {
        var roll = _rng.Next(TotalSessionWeight);
        var sum = 0;
        foreach (var (min, max, weight) in SessionSizeWeights)
        {
            sum += weight;
            if (roll < sum)
                return _rng.Next(min, max + 1);
        }
        return 1;
    }

    private static string GetDomain(int companyIndex) => companyIndex switch
    {
        0 => "dataforge.io",
        1 => "trendhaven.com",
        2 => "homefind.realty",
        3 => "careplus.org",
        4 => "firstbank.com",
        _ => "unknown.com",
    };
}
