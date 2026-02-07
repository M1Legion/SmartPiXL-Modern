using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

/// <summary>
/// Background service that writes tracking data to SQL Server.
/// Implements graceful shutdown - drains queue before stopping.
/// </summary>
public sealed class DatabaseWriterService : BackgroundService
{
    private readonly BlockingCollection<TrackingData> _queue;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly DataTable _dataTableTemplate;
    
    public DatabaseWriterService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _queue = new BlockingCollection<TrackingData>(boundedCapacity: _settings.QueueCapacity);
        _dataTableTemplate = CreateDataTableTemplate();
    }
    
    /// <summary>
    /// Queues tracking data for background write.
    /// Returns immediately - does not block.
    /// </summary>
    /// <returns>True if queued, false if queue is full.</returns>
    public bool TryQueue(TrackingData data)
    {
        if (_queue.IsAddingCompleted)
            return false;
            
        return _queue.TryAdd(data);
    }
    
    /// <summary>
    /// Current queue depth for monitoring.
    /// </summary>
    public int QueueDepth => _queue.Count;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to allow the host to complete startup before we start processing
        await Task.Yield();
        
        _logger.Info($"Database writer started. Queue capacity: {_settings.QueueCapacity}");
        
        var batch = new List<TrackingData>(_settings.BatchSize);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();
            
            try
            {
                // Block until we get an item or cancellation
                if (_queue.TryTake(out var first, _settings.BatchTimeoutMs, stoppingToken))
                {
                    batch.Add(first);
                    
                    // Grab more if available (non-blocking)
                    while (batch.Count < _settings.BatchSize && _queue.TryTake(out var item))
                    {
                        batch.Add(item);
                    }
                    
                    await WriteBatchAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested - will drain below
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error in write loop", ex);
                await Task.Delay(1000, stoppingToken); // Back off on error
            }
        }
        
        // Graceful shutdown: drain remaining queue
        await DrainQueueAsync(batch);
    }
    
    private async Task DrainQueueAsync(List<TrackingData> batch)
    {
        _logger.Info($"Shutting down. Draining {_queue.Count} remaining items...");
        _queue.CompleteAdding();
        
        var timeout = DateTime.UtcNow.AddSeconds(_settings.ShutdownTimeoutSeconds);
        
        while (_queue.Count > 0 && DateTime.UtcNow < timeout)
        {
            batch.Clear();
            
            while (batch.Count < _settings.BatchSize && _queue.TryTake(out var item))
            {
                batch.Add(item);
            }
            
            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch, CancellationToken.None);
            }
        }
        
        if (_queue.Count > 0)
        {
            _logger.Warning($"Shutdown timeout - {_queue.Count} items dropped");
        }
        else
        {
            _logger.Info("Queue drained successfully");
        }
    }
    
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        
        try
        {
            // Clone pre-built schema
            using var table = _dataTableTemplate.Clone();
            
            foreach (var data in batch)
            {
                var row = table.NewRow();
                row["CompanyID"] = (object?)data.CompanyID ?? DBNull.Value;
                row["PiXLID"] = (object?)data.PiXLID ?? DBNull.Value;
                row["IPAddress"] = (object?)data.IPAddress ?? DBNull.Value;
                row["RequestPath"] = (object?)data.RequestPath ?? DBNull.Value;
                row["QueryString"] = (object?)data.QueryString ?? DBNull.Value;
                row["HeadersJson"] = (object?)data.HeadersJson ?? DBNull.Value;
                row["UserAgent"] = (object?)data.UserAgent ?? DBNull.Value;
                row["Referer"] = (object?)data.Referer ?? DBNull.Value;
                row["ReceivedAt"] = data.ReceivedAt;
                table.Rows.Add(row);
            }
            
            // SqlBulkCopy with connection string - ADO.NET handles pooling
            using var bulkCopy = new SqlBulkCopy(_settings.ConnectionString);
            bulkCopy.DestinationTableName = "dbo.PiXL_Test";
            bulkCopy.BatchSize = batch.Count;
            bulkCopy.BulkCopyTimeout = _settings.BulkCopyTimeoutSeconds;
            
            // Column mappings are static - use local function to keep logic close
            AddColumnMappings(bulkCopy);
            
            // WriteToServerAsync for true async I/O
            await bulkCopy.WriteToServerAsync(table, ct);
            
            _logger.Debug($"Wrote {batch.Count} records");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write batch of {batch.Count} records", ex);
        }
        
        return;
        
        // Local function - static to avoid closure allocation
        static void AddColumnMappings(SqlBulkCopy copy)
        {
            copy.ColumnMappings.Add("CompanyID", "CompanyID");
            copy.ColumnMappings.Add("PiXLID", "PiXLID");
            copy.ColumnMappings.Add("IPAddress", "IPAddress");
            copy.ColumnMappings.Add("RequestPath", "RequestPath");
            copy.ColumnMappings.Add("QueryString", "QueryString");
            copy.ColumnMappings.Add("HeadersJson", "HeadersJson");
            copy.ColumnMappings.Add("UserAgent", "UserAgent");
            copy.ColumnMappings.Add("Referer", "Referer");
            copy.ColumnMappings.Add("ReceivedAt", "ReceivedAt");
        }
    }
    
    private static DataTable CreateDataTableTemplate()
    {
        var table = new DataTable();
        table.Columns.Add("CompanyID", typeof(string));
        table.Columns.Add("PiXLID", typeof(string));
        table.Columns.Add("IPAddress", typeof(string));
        table.Columns.Add("RequestPath", typeof(string));
        table.Columns.Add("QueryString", typeof(string));
        table.Columns.Add("HeadersJson", typeof(string));
        table.Columns.Add("UserAgent", typeof(string));
        table.Columns.Add("Referer", typeof(string));
        table.Columns.Add("ReceivedAt", typeof(DateTime));
        return table;
    }
    
    public override void Dispose()
    {
        _queue.Dispose();
        _dataTableTemplate.Dispose();
        base.Dispose();
    }
}
