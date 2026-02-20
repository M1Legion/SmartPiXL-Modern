using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FAILOVER CATCHUP SERVICE — Processes JSONL files written by the Edge when
// the named pipe was unavailable.
//
// ARCHITECTURE:
//   IIS Edge (pipe unavailable) → Failover/*.jsonl files
//   Forge restarts → FailoverCatchupService (this) → reads JSONL files
//       → ForgeChannels.Enrichment → EnrichmentPipelineService → SQL
//
// PROTOCOL:
//   Each JSONL file contains one TrackingData JSON object per line.
//   Files are named with timestamps: failover_2026_02_19.jsonl
//   Processing is line-by-line — malformed lines are skipped, not fatal.
//
// SAFETY:
//   Files are deleted ONLY after all lines have been successfully enqueued.
//   If the channel is full, processing pauses until space is available.
//   Partial processing on shutdown leaves the file intact for next startup.
// ============================================================================

/// <summary>
/// Background service that scans the failover directory for <c>.jsonl</c> files
/// and feeds their contents into the enrichment pipeline for processing.
/// Runs on a timer (default 60s) to catch new failover files.
/// </summary>
public sealed class FailoverCatchupService : BackgroundService
{
    private readonly ForgeSettings _forgeSettings;
    private readonly Channel<TrackingData> _enrichmentChannel;
    private readonly ITrackingLogger _logger;
    private readonly string _failoverDir;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FailoverCatchupService(
        IOptions<ForgeSettings> forgeSettings,
        ForgeChannels channels,
        ITrackingLogger logger)
    {
        _forgeSettings = forgeSettings.Value;
        _enrichmentChannel = channels.Enrichment;
        _logger = logger;

        _failoverDir = Path.IsPathRooted(_forgeSettings.FailoverDirectory)
            ? _forgeSettings.FailoverDirectory
            : Path.Combine(AppContext.BaseDirectory, _forgeSettings.FailoverDirectory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"FailoverCatchupService started. Directory: {_failoverDir}, Interval: {_forgeSettings.FailoverScanIntervalSeconds}s");

        // Initial delay to let the pipe listener and enrichment pipeline stabilize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessFailoverFilesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"FailoverCatchupService error: {ex.Message}");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_forgeSettings.FailoverScanIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Info("FailoverCatchupService stopped.");
    }

    /// <summary>
    /// Scans the failover directory for <c>.jsonl</c> files and processes them
    /// oldest-first. Each file is deleted after successful processing.
    /// </summary>
    private async Task ProcessFailoverFilesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_failoverDir)) return;

        var files = Directory.GetFiles(_failoverDir, "*.jsonl");
        if (files.Length == 0) return;

        _logger.Info($"FailoverCatchup: found {files.Length} JSONL file(s) to process");

        // Process oldest files first
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var processedCount = await ProcessSingleFileAsync(file, ct);

                if (processedCount >= 0)
                {
                    // All lines processed — safe to delete
                    File.Delete(file);
                    if (processedCount > 0)
                        _logger.Info($"FailoverCatchup: processed {processedCount} records from {Path.GetFileName(file)}");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"FailoverCatchup: shutdown during {Path.GetFileName(file)} — file preserved for next scan");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning($"FailoverCatchup: error processing {Path.GetFileName(file)}: {ex.Message}");
                // Leave the file for next scan cycle
            }
        }
    }

    /// <summary>
    /// Processes a single JSONL file line-by-line. Returns the number of records
    /// successfully enqueued, or -1 if processing should be retried (channel full timeout).
    /// </summary>
    private async Task<int> ProcessSingleFileAsync(string filePath, CancellationToken ct)
    {
        var processedCount = 0;
        var skippedCount = 0;

        // Open with FileShare.Read so we don't conflict with the Edge writing
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<TrackingData>(line, s_jsonOpts);
                if (record is null)
                {
                    skippedCount++;
                    continue;
                }

                // Use WriteAsync with a timeout so we don't block forever if the
                // enrichment channel is persistently full
                if (!_enrichmentChannel.Writer.TryWrite(record))
                {
                    // Channel full — wait briefly for space
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        await _enrichmentChannel.Writer.WriteAsync(record, timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.Warning($"FailoverCatchup: enrichment channel full for 30s, pausing file {Path.GetFileName(filePath)}");
                        return -1; // Retry this file next cycle
                    }
                }

                processedCount++;
            }
            catch (JsonException)
            {
                skippedCount++;
                // Skip malformed line, continue processing
            }
        }

        if (skippedCount > 0)
            _logger.Warning($"FailoverCatchup: skipped {skippedCount} malformed lines in {Path.GetFileName(filePath)}");

        return processedCount;
    }
}
