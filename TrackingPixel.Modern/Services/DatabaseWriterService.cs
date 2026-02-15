using System.Collections;
using System.Data.Common;
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
//   4. TrackingDataReader wraps List<TrackingData> directly — zero intermediate allocation
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
//
// WHY DbDataReader INSTEAD OF DataTable?
//   DataTable.Clone() + N DataRow allocations per batch = significant GC pressure.
//   TrackingDataReader wraps the existing List<TrackingData> directly:
//     • Zero intermediate allocations (no DataTable, no DataRow objects)
//     • SqlBulkCopy calls Read() + GetValue() which index the list by ordinal
//     • The list is already in memory — we're just providing a typed view over it
//   For a batch of 100 records, this eliminates ~100 DataRow objects (~200+ bytes each)
//   and the DataTable bookkeeping. Every Gen0 avoided is a win on the hot path.
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
    
    /// <summary>
    /// SQL column names in ordinal order. Must match the column order in <c>PiXL.Test</c>.
    /// <para>
    /// Used for SqlBulkCopy column mapping (ordinal → name).
    /// Stored as a static array to avoid any per-batch allocation.
    /// </para>
    /// </summary>
    internal static readonly string[] ColumnNames =
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
    /// Uses <see cref="TrackingDataReader"/> to wrap the <c>List&lt;TrackingData&gt;</c> directly —
    /// SqlBulkCopy reads from it via <c>Read()</c> + <c>GetValue()</c> calls with zero
    /// intermediate allocations (no DataTable, no DataRow objects).
    /// </para>
    /// </summary>
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        
        try
        {
            // TrackingDataReader wraps the list directly — zero DataTable/DataRow allocations.
            // It implements DbDataReader so SqlBulkCopy.WriteToServerAsync can consume it.
            using var reader = new TrackingDataReader(batch);
            
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
            
            await bulkCopy.WriteToServerAsync(reader, ct);
            
            _logger.Debug($"Wrote {batch.Count} records");
        }
        catch (Exception ex)
        {
            // Log but don't rethrow — the write loop will continue with the next batch.
            // Lost records are acceptable; crashing the service is not.
            _logger.Error($"Failed to write batch of {batch.Count} records", ex);
        }
    }
    
    // ========================================================================
    // TRACKING DATA READER — Zero-allocation DbDataReader over List<TrackingData>
    //
    // Replaces the old DataTable + DataRow approach. SqlBulkCopy calls Read() to
    // advance and GetValue(ordinal) to read fields — both are simple index lookups
    // into the existing list. No intermediate objects are allocated.
    //
    // For a batch of 100: eliminates ~100 DataRow allocations (each ~200+ bytes
    // with internal storage arrays) plus DataTable.Clone() overhead.
    // ========================================================================
    
    /// <summary>
    /// Lightweight <see cref="DbDataReader"/> that wraps a <c>List&lt;TrackingData&gt;</c>
    /// for direct consumption by <see cref="SqlBulkCopy"/>. No intermediate allocations.
    /// <para>
    /// Primary constructor captures the batch list by reference — no copy is made.
    /// <c>_index</c> starts at -1 because <see cref="Read"/> pre-increments before
    /// the first access, matching the ADO.NET reader contract.
    /// </para>
    /// <para>
    /// SqlBulkCopy only ever calls <see cref="Read"/>, <see cref="GetValue"/>,
    /// <see cref="FieldCount"/>, and <see cref="IsDBNull"/>. All other overrides
    /// are mandatory stubs required by the abstract <see cref="DbDataReader"/> base.
    /// </para>
    /// </summary>
    private sealed class TrackingDataReader(List<TrackingData> batch) : DbDataReader
    {
        /// <summary>Current row position. Starts at -1; Read() pre-increments to 0.</summary>
        private int _index = -1;
        
        /// <inheritdoc />
        /// <remarks>9 columns — matches <see cref="ColumnNames"/> array length.</remarks>
        public override int FieldCount => ColumnNames.Length;
        
        /// <inheritdoc />
        /// <remarks>-1 = not applicable for SELECT-style readers (SqlBulkCopy ignores this).</remarks>
        public override int RecordsAffected => -1;
        
        /// <inheritdoc />
        public override bool HasRows => batch.Count > 0;
        
        /// <inheritdoc />
        /// <remarks>Closed when the cursor has advanced past the last row.</remarks>
        public override bool IsClosed => _index >= batch.Count;
        
        /// <inheritdoc />
        /// <remarks>Always 0 — nested result sets are not supported.</remarks>
        public override int Depth => 0;
        
        /// <inheritdoc />
        /// <remarks>
        /// Pre-increments <c>_index</c> and returns true while within bounds.
        /// This is the primary method called by SqlBulkCopy in its inner loop.
        /// </remarks>
        public override bool Read() => ++_index < batch.Count;
        
        /// <inheritdoc />
        /// <remarks>Always false — we have exactly one result set (the batch).</remarks>
        public override bool NextResult() => false;
        
        /// <summary>
        /// Returns the value at the given column ordinal for the current row.
        /// <para>
        /// This is the hot-path method called by SqlBulkCopy for every column
        /// of every row. The switch expression compiles to a jump table —
        /// constant-time dispatch regardless of column count.
        /// </para>
        /// <para>
        /// Nullable string columns return <see cref="DBNull.Value"/> when null,
        /// as required by the ADO.NET contract. ReceivedAt (ordinal 8) is never
        /// null so it returns the <see cref="DateTime"/> directly.
        /// </para>
        /// </summary>
        public override object GetValue(int ordinal)
        {
            var d = batch[_index];
            return ordinal switch
            {
                0 => (object?)d.CompanyID ?? DBNull.Value,     // CompanyID
                1 => (object?)d.PiXLID ?? DBNull.Value,        // PiXLID
                2 => (object?)d.IPAddress ?? DBNull.Value,     // IPAddress
                3 => (object?)d.RequestPath ?? DBNull.Value,   // RequestPath
                4 => (object?)d.QueryString ?? DBNull.Value,   // QueryString
                5 => (object?)d.HeadersJson ?? DBNull.Value,   // HeadersJson
                6 => (object?)d.UserAgent ?? DBNull.Value,     // UserAgent
                7 => (object?)d.Referer ?? DBNull.Value,       // Referer
                8 => d.ReceivedAt,                             // ReceivedAt (never null)
                _ => throw new IndexOutOfRangeException()
            };
        }
        
        /// <summary>
        /// Checks if the value at the given column ordinal is null.
        /// SqlBulkCopy calls this before <see cref="GetValue"/> for nullable columns
        /// to decide whether to write NULL or the actual value to the bulkcopy stream.
        /// </summary>
        public override bool IsDBNull(int ordinal)
        {
            if (ordinal == 8) return false; // ReceivedAt (datetime2) is never null
            var d = batch[_index];
            return ordinal switch
            {
                0 => d.CompanyID is null,
                1 => d.PiXLID is null,
                2 => d.IPAddress is null,
                3 => d.RequestPath is null,
                4 => d.QueryString is null,
                5 => d.HeadersJson is null,
                6 => d.UserAgent is null,
                7 => d.Referer is null,
                _ => true
            };
        }
        
        /// <summary>Gets the column name for the given ordinal (e.g., 0 → "CompanyID").</summary>
        public override string GetName(int ordinal) => ColumnNames[ordinal];
        
        /// <summary>Reverse lookup: column name → ordinal position. O(n) scan of 9 elements.</summary>
        public override int GetOrdinal(string name) => Array.IndexOf(ColumnNames, name);
        
        /// <summary>SQL type name: "datetime2" for ReceivedAt, "nvarchar" for all others.</summary>
        public override string GetDataTypeName(int ordinal) => ordinal == 8 ? "datetime2" : "nvarchar";
        
        /// <summary>.NET type: <see cref="DateTime"/> for ReceivedAt, <see cref="string"/> for all others.</summary>
        public override Type GetFieldType(int ordinal) => ordinal == 8 ? typeof(DateTime) : typeof(string);
        
        /// <summary>Indexer by ordinal — delegates to <see cref="GetValue"/>.</summary>
        public override object this[int ordinal] => GetValue(ordinal);
        
        /// <summary>Indexer by name — resolves ordinal first, then delegates to <see cref="GetValue"/>.</summary>
        public override object this[string name] => GetValue(GetOrdinal(name));
        
        /// <summary>
        /// Fills the provided array with all column values for the current row.
        /// Returns the number of values actually written (min of array length and FieldCount).
        /// </summary>
        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }
        
        /// <summary>Typed string accessor. Throws <see cref="InvalidCastException"/> if the column is null or non-string.</summary>
        public override string GetString(int ordinal)
        {
            var d = batch[_index];
            return (ordinal switch
            {
                0 => d.CompanyID, 1 => d.PiXLID, 2 => d.IPAddress,
                3 => d.RequestPath, 4 => d.QueryString, 5 => d.HeadersJson,
                6 => d.UserAgent, 7 => d.Referer, _ => null
            }) ?? throw new InvalidCastException();
        }
        
        /// <summary>Typed DateTime accessor. Only valid for ordinal 8 (ReceivedAt).</summary>
        public override DateTime GetDateTime(int ordinal) =>
            ordinal == 8 ? batch[_index].ReceivedAt : throw new InvalidCastException();
        
        // ====================================================================
        // ABSTRACT STUBS — Required by DbDataReader but never called by
        // SqlBulkCopy. PiXL.Test has no bool/byte/decimal/float/guid/int
        // columns that would trigger these typed accessors.
        // ====================================================================
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
