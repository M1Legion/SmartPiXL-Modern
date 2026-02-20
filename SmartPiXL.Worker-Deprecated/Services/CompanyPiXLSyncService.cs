using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

// ============================================================================
// COMPANY & PIXL SYNC SERVICE — Full-table sync from Xavier → local tables.
//
// ARCHITECTURE:
//   Xavier (192.168.88.35)                SmartPiXL (localhost\SQL2025)
//   SmartPiXL.dbo.Company (467 rows)  →  SmartPiXL.PiXL.Company
//   SmartPiXL.dbo.PiXL    (5612 rows) →  SmartPiXL.PiXL.Settings
//
// SYNC STRATEGY:
//   Both tables are small enough for a full-table MERGE each cycle.
//   No incremental watermark needed — pull everything, diff via MERGE,
//   apply inserts/updates/deletes. Runs daily alongside IpApiSyncService.
//
// COLUMN MAPPING:
//   Xavier dbo.Company → PiXL.Company: all shared columns by name.
//     Local has extra: Notes, SysStartTime, SysEndTime (temporal, auto-managed).
//   Xavier dbo.PiXL → PiXL.Settings: all shared columns by name.
//     Local has extra: IsActive, ClientParams, ModifiedDate, Notes, SysStartTime, SysEndTime.
//     Extra local-only columns are preserved on UPDATE (not overwritten).
//
// IDENTITY INSERT:
//   PiXL.Company.CompanyID is IDENTITY. We use SET IDENTITY_INSERT ON to
//   preserve the Xavier-assigned CompanyID values.
//
// CONNECTION:
//   Uses XavierSmartPiXLConnectionString for the source.
//   Uses ConnectionString (local) for the target.
// ============================================================================

/// <summary>
/// Background service that syncs Company and PiXL configuration from Xavier's
/// production SmartPiXL database to the local PiXL.Company and PiXL.Settings tables.
/// Runs on a configurable interval (default 6h) staggered from <see cref="IpApiSyncService"/>.
/// </summary>
public sealed class CompanyPiXLSyncService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    public CompanyPiXLSyncService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }
    
    /// <summary>Optional email service injected after construction for guard notifications.</summary>
    public EmailNotificationService? EmailService { get; set; }
    
    /// <summary>
    /// Retries a SQL action up to <paramref name="maxAttempts"/> times on deadlock (error 1205).
    /// Uses jittered exponential backoff: 500ms, 1s, 2s with ±25% jitter.
    /// </summary>
    private async Task<T> WithDeadlockRetryAsync<T>(Func<Task<T>> action, string operationName, int maxAttempts = 3)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (SqlException sqlEx) when (sqlEx.Number == 1205 && attempt < maxAttempts)
            {
                var baseDelay = 500 * (1 << (attempt - 1)); // 500ms, 1s, 2s
                var jitter = Random.Shared.Next(-baseDelay / 4, baseDelay / 4);
                var delay = baseDelay + jitter;
                _logger.Warning($"Deadlock on {operationName} (attempt {attempt}/{maxAttempts}), retrying in {delay}ms");
                await Task.Delay(delay);
            }
        }
    }
    
    /// <summary>
    /// Checks if target tables exist before running MERGE. Returns false and sends
    /// an operator notification if any required table is missing.
    /// </summary>
    private async Task<bool> GuardTablesExistAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                CASE WHEN OBJECT_ID('PiXL.Company', 'U') IS NULL THEN 'PiXL.Company' ELSE '' END +
                CASE WHEN OBJECT_ID('PiXL.Settings', 'U') IS NULL THEN ',PiXL.Settings' ELSE '' END";
        cmd.CommandTimeout = 10;
        
        var result = (await cmd.ExecuteScalarAsync(ct))?.ToString()?.Trim(',');
        if (string.IsNullOrEmpty(result))
            return true; // Both tables exist
            
        var msg = $"CompanyPiXLSync GUARD: missing table(s): {result}. Sync skipped. Create the tables or check the database.";
        _logger.Warning(msg);
        
        if (EmailService is not null)
            await EmailService.NotifyAsync("MissingTable", "Missing sync target tables", msg);
            
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var xavierConnStr = _settings.XavierSmartPiXLConnectionString;
        if (string.IsNullOrEmpty(xavierConnStr))
        {
            _logger.Warning("CompanyPiXLSync: XavierSmartPiXLConnectionString not configured — sync disabled.");
            return;
        }

        _logger.Info($"CompanyPiXLSync started. Interval: {_settings.SyncIntervalHours}h");

        // Stagger slightly after IpApiSync to avoid concurrent load on Xavier
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextSync();
                _logger.Info($"CompanyPiXLSync: next sync in {delay.TotalHours:F1}h");
                await Task.Delay(delay, stoppingToken);

                await RunSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"CompanyPiXLSync failed: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.Info("CompanyPiXLSync stopped.");
    }

    /// <summary>
    /// Calculates delay until the next scheduled sync hour.
    /// Uses SyncIntervalHours (default 6h) with a 3-minute stagger after IpApiSync.
    /// </summary>
    private TimeSpan GetDelayUntilNextSync()
    {
        var intervalHours = Math.Max(1, _settings.SyncIntervalHours);
        return TimeSpan.FromHours(intervalHours);
    }

    /// <summary>
    /// Executes one full sync cycle: pull from Xavier, MERGE into local tables.
    /// Company runs first (parent), then Pixel (child FK depends on Company).
    /// </summary>
    public async Task RunSyncAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        // Guard: verify target tables exist before attempting MERGE
        await using (var guardConn = new SqlConnection(_settings.ConnectionString))
        {
            await guardConn.OpenAsync(ct);
            if (!await GuardTablesExistAsync(guardConn, ct))
                return;
        }

        // Sync Company with logging
        var coSyncId = await InsertSyncLogAsync("Company", ct);
        int coIns = 0, coUpd = 0, coDel = 0;
        try
        {
            (coIns, coUpd, coDel) = await SyncCompanyAsync(ct);
            if (coSyncId > 0)
                await CompleteSyncLogAsync(coSyncId, coIns, coUpd, coDel, null, ct);
        }
        catch (Exception ex)
        {
            if (coSyncId > 0)
                await CompleteSyncLogAsync(coSyncId, coIns, coUpd, coDel, ex.Message, ct);
            throw;
        }

        // Sync Pixel with logging
        var pxSyncId = await InsertSyncLogAsync("Pixel", ct);
        int pxIns = 0, pxUpd = 0, pxDel = 0;
        try
        {
            (pxIns, pxUpd, pxDel) = await SyncPixelAsync(ct);
            if (pxSyncId > 0)
                await CompleteSyncLogAsync(pxSyncId, pxIns, pxUpd, pxDel, null, ct);
        }
        catch (Exception ex)
        {
            if (pxSyncId > 0)
                await CompleteSyncLogAsync(pxSyncId, pxIns, pxUpd, pxDel, ex.Message, ct);
            throw;
        }

        sw.Stop();
        _logger.Info(
            $"CompanyPiXLSync complete in {sw.ElapsedMilliseconds}ms — " +
            $"Company: +{coIns} ~{coUpd} -{coDel}, " +
            $"Pixel: +{pxIns} ~{pxUpd} -{pxDel}");
    }

    /// <summary>
    /// Inserts a sync log entry for a given sync type and returns the SyncId.
    /// </summary>
    private async Task<int> InsertSyncLogAsync(string syncType, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO IPAPI.SyncLog (SyncType)
                OUTPUT INSERTED.SyncId
                VALUES (@SyncType)";
            cmd.Parameters.AddWithValue("@SyncType", syncType);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is int id ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Updates the sync log entry with completion data.
    /// </summary>
    private async Task CompleteSyncLogAsync(int syncLogId, int rowsInserted, int rowsUpdated,
        int rowsDeleted, string? errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE IPAPI.SyncLog SET
                    CompletedAt     = SYSUTCDATETIME(),
                    RowsInserted    = @RowsInserted,
                    RowsUpdated     = @RowsUpdated,
                    RowsDeleted     = @RowsDeleted,
                    DurationMs      = DATEDIFF(MILLISECOND, StartedAt, SYSUTCDATETIME()),
                    ErrorMessage    = @ErrorMessage
                WHERE SyncId = @SyncId";
            cmd.Parameters.AddWithValue("@SyncId", syncLogId);
            cmd.Parameters.AddWithValue("@RowsInserted", rowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", rowsUpdated);
            cmd.Parameters.AddWithValue("@RowsDeleted", rowsDeleted);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Non-critical — don't fail the sync over a log update
        }
    }

    // ========================================================================
    // COMPANY SYNC
    // ========================================================================

    /// <summary>
    /// Full-table MERGE from Xavier dbo.Company → local PiXL.Company.
    /// Returns (inserted, updated, deleted).
    /// </summary>
    private async Task<(int Inserted, int Updated, int Deleted)> SyncCompanyAsync(CancellationToken ct)
    {
        await using var localConn = new SqlConnection(_settings.ConnectionString);
        await localConn.OpenAsync(ct);

        // Create staging table matching the shared columns
        await using (var cmd = localConn.CreateCommand())
        {
            cmd.CommandText = @"
                IF OBJECT_ID('tempdb..#CompanyStaging') IS NOT NULL DROP TABLE #CompanyStaging;
                CREATE TABLE #CompanyStaging (
                    CompanyID            INT          NOT NULL PRIMARY KEY,
                    CompanyName          VARCHAR(100) NULL,
                    ContactName          VARCHAR(100) NULL,
                    Email                VARCHAR(100) NULL,
                    Phone                VARCHAR(50)  NULL,
                    Address              VARCHAR(1000) NULL,
                    City                 VARCHAR(50)  NULL,
                    State                VARCHAR(50)  NULL,
                    Zipcode              VARCHAR(50)  NULL,
                    CompanyTypeId        INT          NULL,
                    TaxId                VARCHAR(50)  NULL,
                    Extension            VARCHAR(50)  NULL,
                    Address2             VARCHAR(1000) NULL,
                    ParentCompanyId      INT          NULL,
                    OriginalParentCoId   INT          NULL,
                    BillingResponsibleFlag BIT        NULL,
                    BillingResponsibleId INT          NULL,
                    NAICS_SIC            VARCHAR(1)   NULL,
                    NAICS_Code           VARCHAR(10)  NULL,
                    SIC_Code             VARCHAR(10)  NULL,
                    PortalURL            VARCHAR(300) NULL,
                    Profit               DECIMAL(18,2) NULL,
                    Cost                 DECIMAL(18,2) NULL,
                    P_Cost               DECIMAL(18,2) NULL,
                    P_Margin             DECIMAL(18,2) NULL,
                    SuspensionDate       DATETIME     NULL,
                    Reasons              VARCHAR(500) NULL,
                    StatusLastUpdated    DATETIME     NULL,
                    PriceListExempt      BIT          NULL,
                    BillingExempt        BIT          NULL,
                    SalesRepresentative  VARCHAR(100) NULL,
                    RampUpPeriod         INT          NULL,
                    RampUpDate           DATE         NULL,
                    MinOrder             INT          NULL,
                    StatusId             INT          NULL,
                    IsActive             BIT          NULL,
                    CreatedDate          DATETIME     NULL,
                    ModifiedDate         DATETIME     NULL
                );";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Bulk copy from Xavier into staging
        int rowsPulled;
        await using (var xavierConn = new SqlConnection(_settings.XavierSmartPiXLConnectionString))
        {
            await xavierConn.OpenAsync(ct);

            await using var pullCmd = xavierConn.CreateCommand();
            pullCmd.CommandText = @"
                SELECT CompanyID, CompanyName, ContactName, Email, Phone,
                       Address, City, State, Zipcode, CompanyTypeId, TaxId,
                       Extension, Address2, ParentCompanyId, OriginalParentCoId,
                       BillingResponsibleFlag, BillingResponsibleId,
                       NAICS_SIC, NAICS_Code, SIC_Code, PortalURL,
                       Profit, Cost, P_Cost, P_Margin,
                       SuspensionDate, Reasons, StatusLastUpdated,
                       PriceListExempt, BillingExempt, SalesRepresentative,
                       RampUpPeriod, RampUpDate, MinOrder,
                       StatusId, IsActive, CreatedDate, ModifiedDate
                FROM dbo.Company";
            pullCmd.CommandTimeout = 60;

            await using var reader = await pullCmd.ExecuteReaderAsync(ct);

            using var bulkCopy = new SqlBulkCopy(localConn)
            {
                DestinationTableName = "#CompanyStaging",
                BulkCopyTimeout = 60,
                BatchSize = 5000
            };

            for (int i = 0; i < 38; i++)
                bulkCopy.ColumnMappings.Add(i, i);

            await bulkCopy.WriteToServerAsync(reader, ct);
            rowsPulled = (int)bulkCopy.RowsCopied;
        }

        _logger.Info($"CompanyPiXLSync: pulled {rowsPulled} companies from Xavier");

        if (rowsPulled == 0)
            return (0, 0, 0);

        // MERGE staging → PiXL.Company (with IDENTITY_INSERT), deadlock-safe
        var (inserted, updated, deleted) = await WithDeadlockRetryAsync(async () =>
        {
            int ins = 0, upd = 0, del = 0;
            await using var mergeCmd = localConn.CreateCommand();
            mergeCmd.CommandText = @"
                SET IDENTITY_INSERT PiXL.Company ON;

                DECLARE @changes TABLE (Action NVARCHAR(10));

                MERGE PiXL.Company AS target
                USING #CompanyStaging AS src ON target.CompanyID = src.CompanyID

                WHEN MATCHED THEN UPDATE SET
                    target.CompanyName          = src.CompanyName,
                    target.ContactName          = src.ContactName,
                    target.Email                = src.Email,
                    target.Phone                = src.Phone,
                    target.Address              = src.Address,
                    target.City                 = src.City,
                    target.State                = src.State,
                    target.Zipcode              = src.Zipcode,
                    target.CompanyTypeId        = src.CompanyTypeId,
                    target.TaxId                = src.TaxId,
                    target.Extension            = src.Extension,
                    target.Address2             = src.Address2,
                    target.ParentCompanyId      = src.ParentCompanyId,
                    target.OriginalParentCoId   = src.OriginalParentCoId,
                    target.BillingResponsibleFlag = src.BillingResponsibleFlag,
                    target.BillingResponsibleId = src.BillingResponsibleId,
                    target.NAICS_SIC            = src.NAICS_SIC,
                    target.NAICS_Code           = src.NAICS_Code,
                    target.SIC_Code             = src.SIC_Code,
                    target.PortalURL            = src.PortalURL,
                    target.Profit               = src.Profit,
                    target.Cost                 = src.Cost,
                    target.P_Cost               = src.P_Cost,
                    target.P_Margin             = src.P_Margin,
                    target.SuspensionDate       = src.SuspensionDate,
                    target.Reasons              = src.Reasons,
                    target.StatusLastUpdated    = src.StatusLastUpdated,
                    target.PriceListExempt      = src.PriceListExempt,
                    target.BillingExempt        = src.BillingExempt,
                    target.SalesRepresentative  = src.SalesRepresentative,
                    target.RampUpPeriod         = src.RampUpPeriod,
                    target.RampUpDate           = src.RampUpDate,
                    target.MinOrder             = src.MinOrder,
                    target.StatusId             = src.StatusId,
                    target.IsActive             = src.IsActive,
                    target.CreatedDate          = src.CreatedDate,
                    target.ModifiedDate         = src.ModifiedDate

                WHEN NOT MATCHED BY TARGET THEN INSERT (
                    CompanyID, CompanyName, ContactName, Email, Phone,
                    Address, City, State, Zipcode, CompanyTypeId, TaxId,
                    Extension, Address2, ParentCompanyId, OriginalParentCoId,
                    BillingResponsibleFlag, BillingResponsibleId,
                    NAICS_SIC, NAICS_Code, SIC_Code, PortalURL,
                    Profit, Cost, P_Cost, P_Margin,
                    SuspensionDate, Reasons, StatusLastUpdated,
                    PriceListExempt, BillingExempt, SalesRepresentative,
                    RampUpPeriod, RampUpDate, MinOrder,
                    StatusId, IsActive, CreatedDate, ModifiedDate)
                VALUES (
                    src.CompanyID, src.CompanyName, src.ContactName, src.Email, src.Phone,
                    src.Address, src.City, src.State, src.Zipcode, src.CompanyTypeId, src.TaxId,
                    src.Extension, src.Address2, src.ParentCompanyId, src.OriginalParentCoId,
                    src.BillingResponsibleFlag, src.BillingResponsibleId,
                    src.NAICS_SIC, src.NAICS_Code, src.SIC_Code, src.PortalURL,
                    src.Profit, src.Cost, src.P_Cost, src.P_Margin,
                    src.SuspensionDate, src.Reasons, src.StatusLastUpdated,
                    src.PriceListExempt, src.BillingExempt, src.SalesRepresentative,
                    src.RampUpPeriod, src.RampUpDate, src.MinOrder,
                    src.StatusId, src.IsActive, src.CreatedDate, src.ModifiedDate)

                WHEN NOT MATCHED BY SOURCE THEN DELETE

                OUTPUT $action INTO @changes;

                SELECT
                    SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Action = 'UPDATE' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Action = 'DELETE' THEN 1 ELSE 0 END)
                FROM @changes;

                SET IDENTITY_INSERT PiXL.Company OFF;

                DROP TABLE #CompanyStaging;";
            mergeCmd.CommandTimeout = 120;

            await using var reader = await mergeCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                ins = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                upd = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                del = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
            return (ins, upd, del);
        }, "Company MERGE");

        return (inserted, updated, deleted);
    }

    // ========================================================================
    // PIXEL SYNC
    // ========================================================================

    /// <summary>
    /// Full-table MERGE from Xavier dbo.PiXL → local PiXL.Settings.
    /// Returns (inserted, updated, deleted).
    /// </summary>
    private async Task<(int Inserted, int Updated, int Deleted)> SyncPixelAsync(CancellationToken ct)
    {
        await using var localConn = new SqlConnection(_settings.ConnectionString);
        await localConn.OpenAsync(ct);

        // Create staging table matching shared columns
        await using (var cmd = localConn.CreateCommand())
        {
            cmd.CommandText = @"
                IF OBJECT_ID('tempdb..#PixelStaging') IS NOT NULL DROP TABLE #PixelStaging;
                CREATE TABLE #PixelStaging (
                    CompanyId       INT           NOT NULL,
                    PiXLId          INT           NOT NULL,
                    PiXLName        VARCHAR(500)  NOT NULL,
                    SmartPiXL       VARCHAR(2000) NULL,
                    PiXLNew         VARCHAR(1000) NULL,
                    PiXLLegacy      VARCHAR(1000) NULL,
                    OutputPathNew   VARCHAR(1000) NULL,
                    OutputPathLegacy VARCHAR(1000) NULL,
                    PiXLContactName VARCHAR(100)  NULL,
                    PiXLContactEmail VARCHAR(100) NULL,
                    PiXLURL         VARCHAR(1000) NULL,
                    Alertnotraffic  BIT           NULL,
                    TimeAlert       VARCHAR(10)   NULL,
                    Zipcode         VARCHAR(50)   NULL,
                    Radius          INT           NULL,
                    Nationwide      BIT           NULL,
                    NumberPage      INT           NULL,
                    TimeSite        TIME          NULL,
                    IncomeRefInitial DECIMAL(18,2) NULL,
                    IncomeRefFinal  DECIMAL(18,2) NULL,
                    InferredCS      VARCHAR(500)  NULL,
                    NetWorth        DECIMAL(18,2) NULL,
                    Married_Y       BIT           NULL,
                    Married_N       BIT           NULL,
                    Married_U       BIT           NULL,
                    Children_Y      BIT           NULL,
                    Children_N      BIT           NULL,
                    Children_U      BIT           NULL,
                    Gender_F        BIT           NULL,
                    Gender_M        BIT           NULL,
                    Gender_U        BIT           NULL,
                    UserId          INT           NULL,
                    StatusId        INT           NULL,
                    SuspendedId     INT           NULL,
                    Reasons         VARCHAR(500)  NULL,
                    CreatedDate     DATETIME      NULL,
                    StatusLastUpdated DATETIME    NULL,
                    PiXLAddress     VARCHAR(500)  NULL,
                    PiXLCity        VARCHAR(50)   NULL,
                    PiXLState       VARCHAR(50)   NULL,
                    PiXLZipCode     VARCHAR(50)   NULL,
                    PiXLPolicyURL   VARCHAR(1000) NULL,
                    PiXLDomain      VARCHAR(8000) NULL,
                    PiXLLatitude    DECIMAL(12,9) NULL,
                    PiXLLongitude   DECIMAL(12,9) NULL,
                    [PiXL 2.5]      VARCHAR(500)  NULL,
                    PRIMARY KEY (CompanyId, PiXLId)
                );";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Bulk copy from Xavier into staging
        int rowsPulled;
        await using (var xavierConn = new SqlConnection(_settings.XavierSmartPiXLConnectionString))
        {
            await xavierConn.OpenAsync(ct);

            await using var pullCmd = xavierConn.CreateCommand();
            pullCmd.CommandText = @"
                SELECT CompanyId, PiXLId, PiXLName, SmartPiXL, PiXLNew,
                       PiXLLegacy, OutputPathNew, OutputPathLegacy,
                       PiXLContactName, PiXLContactEmail, PiXLURL,
                       Alertnotraffic, TimeAlert, Zipcode, Radius,
                       Nationwide, NumberPage, TimeSite,
                       IncomeRefInitial, IncomeRefFinal, InferredCS, NetWorth,
                       Married_Y, Married_N, Married_U,
                       Children_Y, Children_N, Children_U,
                       Gender_F, Gender_M, Gender_U,
                       UserId, StatusId, SuspendedId, Reasons,
                       CreatedDate, StatusLastUpdated,
                       PiXLAddress, PiXLCity, PiXLState, PiXLZipCode,
                       PiXLPolicyURL, PiXLDomain,
                       PiXLLatitude, PiXLLongitude, [PiXL 2.5]
                FROM dbo.PiXL";
            pullCmd.CommandTimeout = 60;

            await using var reader = await pullCmd.ExecuteReaderAsync(ct);

            using var bulkCopy = new SqlBulkCopy(localConn)
            {
                DestinationTableName = "#PixelStaging",
                BulkCopyTimeout = 60,
                BatchSize = 5000
            };

            for (int i = 0; i < 46; i++)
                bulkCopy.ColumnMappings.Add(i, i);

            await bulkCopy.WriteToServerAsync(reader, ct);
            rowsPulled = (int)bulkCopy.RowsCopied;
        }

        _logger.Info($"CompanyPiXLSync: pulled {rowsPulled} PiXLs from Xavier");

        if (rowsPulled == 0)
            return (0, 0, 0);

        // MERGE staging → PiXL.Settings, deadlock-safe
        var (inserted, updated, deleted) = await WithDeadlockRetryAsync(async () =>
        {
            int ins = 0, upd = 0, del = 0;
            await using var mergeCmd = localConn.CreateCommand();
            mergeCmd.CommandText = @"
                DECLARE @changes TABLE (Action NVARCHAR(10));

                MERGE PiXL.Settings AS target
                USING #PixelStaging AS src
                    ON target.CompanyId = src.CompanyId AND target.PiXLId = src.PiXLId

                WHEN MATCHED THEN UPDATE SET
                    target.PiXLName         = src.PiXLName,
                    target.SmartPiXL        = src.SmartPiXL,
                    target.PiXLNew          = src.PiXLNew,
                    target.PiXLLegacy       = src.PiXLLegacy,
                    target.OutputPathNew    = src.OutputPathNew,
                    target.OutputPathLegacy = src.OutputPathLegacy,
                    target.PiXLContactName  = src.PiXLContactName,
                    target.PiXLContactEmail = src.PiXLContactEmail,
                    target.PiXLURL          = src.PiXLURL,
                    target.Alertnotraffic   = src.Alertnotraffic,
                    target.TimeAlert        = src.TimeAlert,
                    target.Zipcode          = src.Zipcode,
                    target.Radius           = src.Radius,
                    target.Nationwide       = src.Nationwide,
                    target.NumberPage       = src.NumberPage,
                    target.TimeSite         = src.TimeSite,
                    target.IncomeRefInitial = src.IncomeRefInitial,
                    target.IncomeRefFinal   = src.IncomeRefFinal,
                    target.InferredCS       = src.InferredCS,
                    target.NetWorth         = src.NetWorth,
                    target.Married_Y        = src.Married_Y,
                    target.Married_N        = src.Married_N,
                    target.Married_U        = src.Married_U,
                    target.Children_Y       = src.Children_Y,
                    target.Children_N       = src.Children_N,
                    target.Children_U       = src.Children_U,
                    target.Gender_F         = src.Gender_F,
                    target.Gender_M         = src.Gender_M,
                    target.Gender_U         = src.Gender_U,
                    target.UserId           = src.UserId,
                    target.StatusId         = src.StatusId,
                    target.SuspendedId      = src.SuspendedId,
                    target.Reasons          = src.Reasons,
                    target.CreatedDate      = src.CreatedDate,
                    target.StatusLastUpdated = src.StatusLastUpdated,
                    target.PiXLAddress      = src.PiXLAddress,
                    target.PiXLCity         = src.PiXLCity,
                    target.PiXLState        = src.PiXLState,
                    target.PiXLZipCode      = src.PiXLZipCode,
                    target.PiXLPolicyURL    = src.PiXLPolicyURL,
                    target.PiXLDomain       = src.PiXLDomain,
                    target.PiXLLatitude     = src.PiXLLatitude,
                    target.PiXLLongitude    = src.PiXLLongitude,
                    target.[PiXL 2.5]       = src.[PiXL 2.5]

                WHEN NOT MATCHED BY TARGET THEN INSERT (
                    CompanyId, PiXLId, PiXLName, SmartPiXL, PiXLNew,
                    PiXLLegacy, OutputPathNew, OutputPathLegacy,
                    PiXLContactName, PiXLContactEmail, PiXLURL,
                    Alertnotraffic, TimeAlert, Zipcode, Radius,
                    Nationwide, NumberPage, TimeSite,
                    IncomeRefInitial, IncomeRefFinal, InferredCS, NetWorth,
                    Married_Y, Married_N, Married_U,
                    Children_Y, Children_N, Children_U,
                    Gender_F, Gender_M, Gender_U,
                    UserId, StatusId, SuspendedId, Reasons,
                    CreatedDate, StatusLastUpdated,
                    PiXLAddress, PiXLCity, PiXLState, PiXLZipCode,
                    PiXLPolicyURL, PiXLDomain,
                    PiXLLatitude, PiXLLongitude, [PiXL 2.5])
                VALUES (
                    src.CompanyId, src.PiXLId, src.PiXLName, src.SmartPiXL, src.PiXLNew,
                    src.PiXLLegacy, src.OutputPathNew, src.OutputPathLegacy,
                    src.PiXLContactName, src.PiXLContactEmail, src.PiXLURL,
                    src.Alertnotraffic, src.TimeAlert, src.Zipcode, src.Radius,
                    src.Nationwide, src.NumberPage, src.TimeSite,
                    src.IncomeRefInitial, src.IncomeRefFinal, src.InferredCS, src.NetWorth,
                    src.Married_Y, src.Married_N, src.Married_U,
                    src.Children_Y, src.Children_N, src.Children_U,
                    src.Gender_F, src.Gender_M, src.Gender_U,
                    src.UserId, src.StatusId, src.SuspendedId, src.Reasons,
                    src.CreatedDate, src.StatusLastUpdated,
                    src.PiXLAddress, src.PiXLCity, src.PiXLState, src.PiXLZipCode,
                    src.PiXLPolicyURL, src.PiXLDomain,
                    src.PiXLLatitude, src.PiXLLongitude, src.[PiXL 2.5])

                WHEN NOT MATCHED BY SOURCE THEN DELETE

                OUTPUT $action INTO @changes;

                SELECT
                    SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Action = 'UPDATE' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Action = 'DELETE' THEN 1 ELSE 0 END)
                FROM @changes;

                DROP TABLE #PixelStaging;";
            mergeCmd.CommandTimeout = 120;

            await using var reader = await mergeCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                ins = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                upd = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                del = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
            return (ins, upd, del);
        }, "PiXL MERGE");

        return (inserted, updated, deleted);
    }
}
