using System.Data;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

// ============================================================================
// DATABASE WRITER SERVICE — Async bulk writer backed by Channel<T>.
//
// ARCHITECTURE:
//   TrackingEndpoints  →  TryQueue()  →  Channel<TrackingData>  →  ExecuteAsync()  →  SqlBulkCopy  →  PiXL.Test
//   (HTTP thread)         (CAS write)     (bounded buffer)         (single reader)     (ADO.NET)        (SQL Server)
//
// WHY Channel<T>?
//   • Lock-free: TryWrite is a single Compare-And-Swap (CAS) operation — no mutex
//   • Native async: WaitToReadAsync doesn't burn a thread while waiting
//   • ~3× faster than BlockingCollection<T> for single-consumer patterns
//   • Built-in backpressure: BoundedChannelFullMode.Wait means TryWrite returns
//     false immediately when full (the HTTP endpoint logs + drops the request)
//
// IMPORTANT: BoundedChannelFullMode.Wait
//   • TryWrite() returns FALSE immediately when the buffer is full
//   • WriteAsync() would BLOCK (await) until space is available
//   • We use TryWrite, so callers are never blocked — they get a false return
//   • DropWrite would silently accept and discard the item (TryWrite returns true!)
//   • This is a critical distinction tested by TryQueue_FullQueue_ReturnsFalse
//
// BATCHING STRATEGY:
//   1. Async wait for at least one item (no CPU burn)
//   2. Synchronous drain: read up to BatchSize items that are already buffered
//   3. Write entire batch via SqlBulkCopy (single round-trip to SQL Server)
//   4. DataTable.Clone() copies only schema, not rows — cheap per batch
//
// SHUTDOWN BEHAVIOR:
//   1. CancellationToken fires → main loop exits
//   2. DrainChannelAsync() signals channel complete, reads remaining items
//   3. Writes final batches with CancellationToken.None (deadline-bounded)
//   4. Logs warning if items remain after timeout
//
// SQL COLUMN MAPPING:
//   Uses ordinal mapping (row[0], row[1], ...) instead of name-based mapping.
//   This avoids a Dictionary<string,int> lookup inside SqlBulkCopy for every row.
//   Column order MUST match the ColumnNames array exactly.
// ============================================================================

/// <summary>
/// Background service that consumes <see cref="TrackingData"/> from a bounded channel
/// and bulk-writes batches to <c>PiXL.Test</c> via <see cref="SqlBulkCopy"/>.
/// <para>
/// Inherits from <see cref="BackgroundService"/> which handles the hosted service lifecycle
/// (StartAsync/StopAsync/Dispose). Only <see cref="ExecuteAsync"/> needs to be overridden.
/// </para>
/// </summary>
public sealed class DatabaseWriterService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly DataTable _dataTableTemplate;
    
    /// <summary>
    /// SQL column names in ordinal order. Must match the column order in <c>PiXL.Test</c>.
    /// <para>
    /// Used for two purposes:
    /// <list type="number">
    ///   <item><description>DataTable schema creation (CreateDataTableTemplate)</description></item>
    ///   <item><description>SqlBulkCopy column mapping (ordinal → name)</description></item>
    /// </list>
    /// Stored as a static array to avoid any per-batch allocation.
    /// </para>
    /// </summary>
    private static readonly string[] ColumnNames =
    [
        "CompanyID",    // [0] nvarchar — client identifier from URL path
        "PiXLID",       // [1] nvarchar — campaign/pixel identifier from URL path
        "IPAddress",    // [2] nvarchar — real client IP (after proxy header extraction)
        "RequestPath",  // [3] nvarchar — full URL path (e.g., /ACME/summer2025_SMART.GIF)
        "QueryString",  // [4] nvarchar(max) — all ~90 JS-collected parameters
        "HeadersJson",  // [5] nvarchar(max) — JSON object of captured HTTP headers
        "UserAgent",    // [6] nvarchar(2000) — truncated User-Agent string
        "Referer",      // [7] nvarchar(2000) — truncated Referer URL
        "ReceivedAt"    // [8] datetime2 — UTC timestamp when the hit arrived
    ];

    public DatabaseWriterService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
        
        // Create a bounded channel with Wait policy.
        // Wait means: TryWrite returns false when full; WriteAsync would block.
        // We use TryWrite exclusively, so HTTP threads are never blocked.
        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(_settings.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full
                SingleReader = true     // Only ExecuteAsync reads — enables lock-free fast path
            });
        
        // Pre-build the DataTable schema once. Clone() in WriteBatchAsync copies
        // only the column definitions, not any row data — cheap per batch.
        _dataTableTemplate = CreateDataTableTemplate();
    }
    
    /// <summary>
    /// Enqueues a tracking record for asynchronous database write.
    /// <para>
    /// Lock-free CAS operation under the hood. Returns immediately:
    /// <list type="bullet">
    ///   <item><description><c>true</c> — item accepted into the channel buffer</description></item>
    ///   <item><description><c>false</c> — channel is full (caller should log and drop)</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryQueue(TrackingData data) => _channel.Writer.TryWrite(data);
    
    /// <summary>
    /// Current number of items buffered in the channel, waiting to be written.
    /// Exposed for the <c>/health</c> endpoint monitoring.
    /// </summary>
    public int QueueDepth => _channel.Reader.Count;
    
    /// <summary>
    /// Main processing loop. Runs on a dedicated background thread for the lifetime
    /// of the application. Exits cleanly when the host signals shutdown.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task.Yield() releases the synchronous startup path so the host can finish
        // registering other services before we start processing. Without this, a
        // slow SQL connection on first batch could delay the entire app startup.
        await Task.Yield();
        
        _logger.Info($"Database writer started. Queue capacity: {_settings.QueueCapacity}");
        
        var batch = new List<TrackingData>(_settings.BatchSize);
        var reader = _channel.Reader;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();
            
            try
            {
                // WaitToReadAsync: async wait that doesn't burn a thread.
                // Returns true when at least one item is available, false when
                // the channel is completed (no more items will ever arrive).
                if (await reader.WaitToReadAsync(stoppingToken))
                {
                    // Synchronous drain: items are already in the channel's internal buffer,
                    // so TryRead is just a pointer swap — no await needed. We drain up to
                    // BatchSize items to maximize the SqlBulkCopy batch efficiency.
                    while (batch.Count < _settings.BatchSize && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }
                    
                    if (batch.Count > 0)
                        await WriteBatchAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Host is shutting down — fall through to drain
            }
            catch (Exception ex)
            {
                // Don't crash the background service on transient SQL errors.
                // Log and retry after a short delay to avoid a tight error loop.
                _logger.Error("Unexpected error in write loop", ex);
                await Task.Delay(1000, stoppingToken);
            }
        }
        
        // Graceful shutdown: drain any remaining items before the process exits
        await DrainChannelAsync(batch);
    }
    
    /// <summary>
    /// Drains remaining channel items during graceful shutdown.
    /// <para>
    /// Signals the channel as complete (no more writes accepted), then reads
    /// remaining items in batches until either the channel is empty or the
    /// shutdown timeout expires. Uses <see cref="CancellationToken.None"/>
    /// for the final writes because the original token is already cancelled.
    /// </para>
    /// </summary>
    private async Task DrainChannelAsync(List<TrackingData> batch)
    {
        _channel.Writer.TryComplete();
        _logger.Info($"Shutting down. Draining remaining items...");
        
        var deadline = DateTime.UtcNow.AddSeconds(_settings.ShutdownTimeoutSeconds);
        
        while (DateTime.UtcNow < deadline)
        {
            batch.Clear();
            
            while (batch.Count < _settings.BatchSize && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            
            if (batch.Count > 0)
                await WriteBatchAsync(batch, CancellationToken.None);
            else
                break; // Channel is empty — nothing left to drain
        }
        
        var remaining = _channel.Reader.Count;
        if (remaining > 0)
            _logger.Warning($"Shutdown timeout - {remaining} items dropped");
        else
            _logger.Info("Queue drained successfully");
    }
    
    /// <summary>
    /// Writes a batch of tracking records to <c>PiXL.Test</c> via <see cref="SqlBulkCopy"/>.
    /// <para>
    /// Steps:
    /// <list type="number">
    ///   <item><description>Clone the pre-built DataTable schema (cheap — copies columns, not rows)</description></item>
    ///   <item><description>Populate rows using ordinal indexing (row[0], row[1], ...)</description></item>
    ///   <item><description>Open a pooled SQL connection (ADO.NET handles connection pooling)</description></item>
    ///   <item><description>Configure SqlBulkCopy with ordinal column mappings</description></item>
    ///   <item><description>WriteToServerAsync sends the entire DataTable in one round-trip</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        
        try
        {
            // Clone() copies the schema (column definitions) without any row data.
            // This is much cheaper than building a new DataTable from scratch each batch.
            using var table = _dataTableTemplate.Clone();
            
            foreach (var data in batch)
            {
                var row = table.NewRow();
                // Ordinal assignment — avoids the string-keyed Dictionary lookup that
                // row["ColumnName"] would require. AsDbValue converts null → DBNull.Value.
                row[0] = AsDbValue(data.CompanyID);
                row[1] = AsDbValue(data.PiXLID);
                row[2] = AsDbValue(data.IPAddress);
                row[3] = AsDbValue(data.RequestPath);
                row[4] = AsDbValue(data.QueryString);
                row[5] = AsDbValue(data.HeadersJson);
                row[6] = AsDbValue(data.UserAgent);
                row[7] = AsDbValue(data.Referer);
                row[8] = data.ReceivedAt; // DateTime — not nullable, no DBNull check needed
                table.Rows.Add(row);
            }
            
            // SqlBulkCopy with connection string — ADO.NET manages the connection pool.
            // We don't keep a persistent connection; each batch gets a pooled connection.
            using var bulkCopy = new SqlBulkCopy(_settings.ConnectionString);
            bulkCopy.DestinationTableName = "PiXL.Test";
            bulkCopy.BatchSize = batch.Count;
            bulkCopy.BulkCopyTimeout = _settings.BulkCopyTimeoutSeconds;
            
            // Map by ordinal position — avoids string dictionary lookups inside SqlBulkCopy
            // for every row during WriteToServer. Column order must match ColumnNames array.
            var cols = ColumnNames;
            for (var i = 0; i < cols.Length; i++)
                bulkCopy.ColumnMappings.Add(i, cols[i]);
            
            await bulkCopy.WriteToServerAsync(table, ct);
            
            _logger.Debug($"Wrote {batch.Count} records");
        }
        catch (Exception ex)
        {
            // Log but don't rethrow — the write loop will continue with the next batch.
            // Lost records are acceptable; crashing the service is not.
            _logger.Error($"Failed to write batch of {batch.Count} records", ex);
        }
    }

    /// <summary>
    /// Converts a nullable string to <see cref="DBNull.Value"/> when null.
    /// <para>
    /// <c>[MethodImpl(AggressiveInlining)]</c> ensures the JIT eliminates the method call
    /// overhead — this compiles to a simple null-coalescing check at each call site.
    /// SqlBulkCopy requires DBNull.Value, not C# null, for nullable columns.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object AsDbValue(string? value) => (object?)value ?? DBNull.Value;
    
    /// <summary>
    /// Creates the DataTable schema template used by <see cref="WriteBatchAsync"/>.
    /// <para>
    /// All columns are <c>typeof(string)</c> except <c>ReceivedAt</c> which is <c>typeof(DateTime)</c>.
    /// This template is created once in the constructor and cloned per batch.
    /// </para>
    /// </summary>
    private static DataTable CreateDataTableTemplate()
    {
        var table = new DataTable();
        var cols = ColumnNames;
        // Add all string columns (indices 0–7)
        for (var i = 0; i < cols.Length - 1; i++)
            table.Columns.Add(cols[i], typeof(string));
        // Last column (ReceivedAt) is DateTime, not string
        table.Columns.Add(cols[^1], typeof(DateTime));
        return table;
    }
    
    /// <summary>
    /// Disposes the DataTable template. Called by <see cref="BackgroundService.Dispose"/>.
    /// BackgroundService already implements IDisposable, so we just override and chain.
    /// </summary>
    public override void Dispose()
    {
        _dataTableTemplate.Dispose();
        base.Dispose();
    }
}
