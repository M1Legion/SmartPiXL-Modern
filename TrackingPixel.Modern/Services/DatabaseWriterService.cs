using System.Data;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

/// <summary>
/// Background service that writes tracking data to SQL Server via Channel&lt;T&gt;.
/// Channel is lock-free (CAS-based), native async, and ~3x faster than BlockingCollection.
/// Implements graceful shutdown - drains channel before stopping.
/// </summary>
public sealed class DatabaseWriterService : BackgroundService, IDisposable
{
    private readonly Channel<TrackingData> _channel;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly DataTable _dataTableTemplate;
    
    // Column name spans — stackalloc-friendly, zero heap alloc for mappings
    private static readonly string[] ColumnNames =
    [
        "CompanyID", "PiXLID", "IPAddress", "RequestPath",
        "QueryString", "HeadersJson", "UserAgent", "Referer", "ReceivedAt"
    ];

    public DatabaseWriterService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(_settings.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full (matches old TryAdd semantics)
                SingleReader = true     // Only ExecuteAsync reads — enables lock-free fast path
            });
        _dataTableTemplate = CreateDataTableTemplate();
    }
    
    /// <summary>
    /// Queues tracking data for background write.
    /// Returns immediately — lock-free CAS under the hood.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryQueue(TrackingData data) => _channel.Writer.TryWrite(data);
    
    /// <summary>
    /// Current queue depth for monitoring.
    /// </summary>
    public int QueueDepth => _channel.Reader.Count;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to allow the host to complete startup before we start processing
        await Task.Yield();
        
        _logger.Info($"Database writer started. Queue capacity: {_settings.QueueCapacity}");
        
        var batch = new List<TrackingData>(_settings.BatchSize);
        var reader = _channel.Reader;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();
            
            try
            {
                // Async wait for first item — no thread burn
                if (await reader.WaitToReadAsync(stoppingToken))
                {
                    // Drain up to BatchSize synchronously (items already buffered)
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
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error in write loop", ex);
                await Task.Delay(1000, stoppingToken);
            }
        }
        
        // Graceful shutdown: drain remaining channel
        await DrainChannelAsync(batch);
    }
    
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
                break; // Nothing left
        }
        
        var remaining = _channel.Reader.Count;
        if (remaining > 0)
            _logger.Warning($"Shutdown timeout - {remaining} items dropped");
        else
            _logger.Info("Queue drained successfully");
    }
    
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        
        try
        {
            // Clone pre-built schema — DataTable.Clone() is cheap (copies schema, not rows)
            using var table = _dataTableTemplate.Clone();
            
            foreach (var data in batch)
            {
                var row = table.NewRow();
                // Bitwise trick: (object?)s ?? DBNull.Value is 3 IL ops per field.
                // Using helper that JIT inlines to avoid repetition.
                row[0] = AsDbValue(data.CompanyID);
                row[1] = AsDbValue(data.PiXLID);
                row[2] = AsDbValue(data.IPAddress);
                row[3] = AsDbValue(data.RequestPath);
                row[4] = AsDbValue(data.QueryString);
                row[5] = AsDbValue(data.HeadersJson);
                row[6] = AsDbValue(data.UserAgent);
                row[7] = AsDbValue(data.Referer);
                row[8] = data.ReceivedAt;
                table.Rows.Add(row);
            }
            
            // SqlBulkCopy with connection string - ADO.NET handles pooling
            using var bulkCopy = new SqlBulkCopy(_settings.ConnectionString);
            bulkCopy.DestinationTableName = "dbo.PiXL_Test";
            bulkCopy.BatchSize = batch.Count;
            bulkCopy.BulkCopyTimeout = _settings.BulkCopyTimeoutSeconds;
            
            // Map by ordinal — avoids string dictionary lookups inside SqlBulkCopy
            var cols = ColumnNames;
            for (var i = 0; i < cols.Length; i++)
                bulkCopy.ColumnMappings.Add(i, cols[i]);
            
            await bulkCopy.WriteToServerAsync(table, ct);
            
            _logger.Debug($"Wrote {batch.Count} records");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write batch of {batch.Count} records", ex);
        }
    }

    /// <summary>
    /// Converts nullable string to DBNull.Value when null.
    /// JIT inlines this — no method call overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object AsDbValue(string? value) => (object?)value ?? DBNull.Value;
    
    private static DataTable CreateDataTableTemplate()
    {
        var table = new DataTable();
        var cols = ColumnNames;
        // Add all string columns, then override ReceivedAt to DateTime
        for (var i = 0; i < cols.Length - 1; i++)
            table.Columns.Add(cols[i], typeof(string));
        table.Columns.Add(cols[^1], typeof(DateTime));
        return table;
    }
    
    public override void Dispose()
    {
        _dataTableTemplate.Dispose();
        base.Dispose();
    }
}
