using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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
//   When the enrichment channel is full, WriteAsync blocks (backpressure)
//   up to 5 seconds. If still full, the record is logged and dropped —
//   the Edge has its own JSONL failover for pipe-unavailable scenarios.
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
    private readonly ForgeMetrics _metrics;
    private readonly string _deadLetterDir;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Lock for dead-letter file writes.</summary>
    private readonly object _deadLetterLock = new();

    public PipeListenerService(
        IOptions<ForgeSettings> forgeSettings,
        ForgeChannels channels,
        ITrackingLogger logger,
        ForgeMetrics metrics)
    {
        _forgeSettings = forgeSettings.Value;
        _enrichmentChannel = channels.Enrichment;
        _logger = logger;
        _metrics = metrics;

        // Dead-letter files go in the failover directory alongside failover files
        _deadLetterDir = Path.IsPathRooted(_forgeSettings.FailoverDirectory)
            ? _forgeSettings.FailoverDirectory
            : Path.Combine(AppContext.BaseDirectory, _forgeSettings.FailoverDirectory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var pipeName = _forgeSettings.PipeName;
        var instanceCount = _forgeSettings.MaxConcurrentPipeInstances;

        _metrics.SamplePipeListenerState(true);
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
                // PipeSecurity: grant the IIS app pool identity read access
                // so the Edge (running as IIS APPPOOL\Smartpixl.info) can connect.
                // TODO: Update pool name to "Smartpixl.com" when domain migrates (~6 months)
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new NTAccount("IIS APPPOOL", "Smartpixl.info"),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                await using var pipeServer = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.In,
                    _forgeSettings.MaxConcurrentPipeInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);

                _logger.Debug($"Pipe instance {instanceId}: waiting for connection...");
                await pipeServer.WaitForConnectionAsync(ct);
                _logger.Debug($"Pipe instance {instanceId}: client connected.");
                _metrics.RecordPipeConnect();

                try
                {
                    await ReadRecordsAsync(pipeServer, instanceId, ct);
                }
                finally
                {
                    _metrics.RecordPipeDisconnect();
                }
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
    /// Uses <see cref="ChannelWriter{T}.WriteAsync"/> for backpressure — when the
    /// enrichment channel is full, reading pauses until space is available (up to 5s).
    /// This applies natural TCP-level backpressure to the Edge's pipe client.
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
                    var ts = ForgeMetrics.StartTimer();

                    var record = JsonSerializer.Deserialize<TrackingData>(line, s_jsonOpts);
                    if (record is null)
                    {
                        _logger.Warning($"Pipe instance {instanceId}: deserialized null record, skipping");
                        continue;
                    }

                    // WriteAsync with timeout — applies backpressure when channel is full.
                    // BoundedChannelFullMode.Wait causes WriteAsync to block until space
                    // is available. The 5s timeout prevents indefinite blocking if the
                    // enrichment pipeline is completely stalled.
                    try
                    {
                        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        writeCts.CancelAfter(_forgeSettings.PipeChannelWriteTimeoutMs);
                        await _enrichmentChannel.Writer.WriteAsync(record, writeCts.Token);
                        _metrics.Record(Stage.PipeDeserialize, ts);
                        recordCount++;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout expired — enrichment channel is critically backed up.
                        // Edge has its own JSONL failover if we can't keep up.
                        _metrics.RecordDrop(Stage.PipeDeserialize);
                        _logger.Warning($"Pipe instance {instanceId}: enrichment channel full for {_forgeSettings.PipeChannelWriteTimeoutMs}ms — dropping record (Edge failover handles persistence)");
                    }
                }
                catch (JsonException ex)
                {
                    _logger.Warning($"Pipe instance {instanceId}: malformed JSON — {ex.Message}");
                    // Preserve malformed line in dead-letter file
                    WriteToDeadLetter(line, $"pipe_instance_{instanceId}");
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
    /// Writes a raw line to a dead-letter file so malformed pipe data is never lost.
    /// </summary>
    private void WriteToDeadLetter(string rawLine, string source)
    {
        lock (_deadLetterLock)
        {
            try
            {
                Directory.CreateDirectory(_deadLetterDir);
                var date = DateTime.UtcNow.ToString("yyyy_MM_dd");
                var deadLetterPath = Path.Combine(_deadLetterDir, $"dead_letter_{date}.jsonl");
                var entry = $"// Source: {source} at {DateTime.UtcNow:O}" + Environment.NewLine + rawLine;
                File.AppendAllText(deadLetterPath, entry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.Error($"PipeListener: failed to write dead-letter: {ex.Message}");
            }
        }
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
