using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// PIPE LISTENER SERVICE — Named pipe server that receives TrackingData from
// the IIS Edge process.
//
// ARCHITECTURE:
//   IIS Edge (PipeClientService)
//       → NamedPipeClientStream("SmartPiXL-Enrichment")
//       → JSON line per TrackingData record
//       → NamedPipeServerStream (this service)
//       → ForgeChannels.Enrichment → EnrichmentPipelineService
//
// CONCURRENCY:
//   Runs N concurrent pipe server instances (default 4) so the Edge can
//   reconnect immediately if one instance is busy processing a read.
//   Each instance runs in its own Task via Task.Run.
//
// PROTOCOL:
//   One JSON line per TrackingData record, terminated by \n (newline).
//   UTF-8 encoded. The Edge writes one record at a time and flushes.
//   The pipe is byte-mode (not message-mode) for simplicity.
//
// RESILIENCE:
//   Each pipe instance auto-reconnects on disconnect. Malformed JSON lines
//   are logged and skipped — one bad record never crashes the listener.
// ============================================================================

/// <summary>
/// Background service that hosts multiple <see cref="NamedPipeServerStream"/>
/// instances to receive <see cref="TrackingData"/> records from the IIS Edge process.
/// Records are deserialized and enqueued to the enrichment channel via
/// <see cref="ForgeChannels"/>.
/// </summary>
public sealed class PipeListenerService : BackgroundService
{
    private readonly ForgeSettings _forgeSettings;
    private readonly Channel<TrackingData> _enrichmentChannel;
    private readonly ITrackingLogger _logger;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PipeListenerService(
        IOptions<ForgeSettings> forgeSettings,
        ForgeChannels channels,
        ITrackingLogger logger)
    {
        _forgeSettings = forgeSettings.Value;
        _enrichmentChannel = channels.Enrichment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var pipeName = _forgeSettings.PipeName;
        var instanceCount = _forgeSettings.MaxConcurrentPipeInstances;

        _logger.Info($"PipeListenerService started. Pipe: {pipeName}, Instances: {instanceCount}");

        // Launch N concurrent pipe server instances
        var tasks = new Task[instanceCount];
        for (var i = 0; i < instanceCount; i++)
        {
            var instanceId = i;
            tasks[i] = Task.Run(() => RunPipeInstanceAsync(pipeName, instanceId, stoppingToken), stoppingToken);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info("PipeListenerService stopped.");
    }

    /// <summary>
    /// Runs a single pipe server instance in a loop: wait for connection,
    /// read records, disconnect, repeat.
    /// </summary>
    private async Task RunPipeInstanceAsync(string pipeName, int instanceId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    _forgeSettings.MaxConcurrentPipeInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _logger.Debug($"Pipe instance {instanceId}: waiting for connection...");
                await pipeServer.WaitForConnectionAsync(ct);
                _logger.Debug($"Pipe instance {instanceId}: client connected.");

                await ReadRecordsAsync(pipeServer, instanceId, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.Warning($"Pipe instance {instanceId}: IO error — {ex.Message}");
                // Brief delay before reconnecting to avoid tight error loop
                await SafeDelayAsync(TimeSpan.FromSeconds(1), ct);
            }
            catch (Exception ex)
            {
                _logger.Error($"Pipe instance {instanceId}: unexpected error — {ex.Message}");
                await SafeDelayAsync(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    /// <summary>
    /// Reads JSON lines from the connected pipe stream, deserializes each into
    /// a <see cref="TrackingData"/> record, and enqueues it to the enrichment channel.
    /// Continues until the client disconnects or cancellation is requested.
    /// </summary>
    private async Task ReadRecordsAsync(NamedPipeServerStream pipe, int instanceId, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var recordCount = 0;

        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break; // Client disconnected (EOF)

                if (line.Length == 0)
                    continue; // Skip empty lines

                try
                {
                    var record = JsonSerializer.Deserialize<TrackingData>(line, s_jsonOpts);
                    if (record is null)
                    {
                        _logger.Warning($"Pipe instance {instanceId}: deserialized null record, skipping");
                        continue;
                    }

                    if (!_enrichmentChannel.Writer.TryWrite(record))
                    {
                        _logger.Warning($"Pipe instance {instanceId}: enrichment channel full, dropping record");
                    }
                    else
                    {
                        recordCount++;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.Warning($"Pipe instance {instanceId}: malformed JSON — {ex.Message}");
                    // Skip malformed line, continue reading
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected mid-read — normal during Edge restart
        }

        if (recordCount > 0)
            _logger.Debug($"Pipe instance {instanceId}: session ended, received {recordCount} records");
    }

    /// <summary>
    /// Delays without throwing on cancellation.
    /// </summary>
    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }
}
