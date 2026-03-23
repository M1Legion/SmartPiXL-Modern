using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// COMPANY & PIXL SYNC SERVICE — Full-table sync from Xavier → local tables.
//
// Ported from SmartPiXL.Worker-Deprecated/Services/CompanyPiXLSyncService.cs
// with namespace updated to SmartPiXL.Forge.Services.
//
// ARCHITECTURE:
//   Xavier (192.168.88.35)                SmartPiXL (localhost\SQL2025)
//   SmartPiXL.dbo.Company (467 rows)  →  SmartPiXL.PiXL.Company
//   SmartPiXL.dbo.PiXL    (5612 rows) →  SmartPiXL.PiXL.Settings
//
// SYNC STRATEGY:
//   Both tables are small enough for a full-table sync each cycle.
//   Uses UPDATE + INSERT + DELETE (no MERGE).
//   Company runs first (parent), then Pixel (child FK).
// ============================================================================

/// <summary>
/// Background service that syncs Company and PiXL configuration from Xavier's
/// production SmartPiXL database to the local PiXL.Company and PiXL.Settings tables.
/// Runs on a configurable interval (default 6h).
/// </summary>
public sealed class CompanyPiXLSyncService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly ForgeMetrics _metrics;

    public CompanyPiXLSyncService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger,
        ForgeMetrics metrics)
    {
        _settings = settings.Value;
        _logger = logger;
        _metrics = metrics;
    }



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
                var baseDelay = 500 * (1 << (attempt - 1));
                var jitter = Random.Shared.Next(-baseDelay / 4, baseDelay / 4);
                var delay = baseDelay + jitter;
                _logger.Warning($"Deadlock on {operationName} (attempt {attempt}/{maxAttempts}), retrying in {delay}ms");
                await Task.Delay(delay);
            }
        }
    }

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
            return true;

        _logger.Warning($"CompanyPiXLSync GUARD: missing table(s): {result}. Sync skipped.");
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

        // Stagger startup to avoid contention with other background services
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSyncAsync(stoppingToken);

                var delay = TimeSpan.FromHours(Math.Max(1, _settings.SyncIntervalHours));
                _logger.Info($"CompanyPiXLSync: next sync in {delay.TotalHours:F1}h");
                await Task.Delay(delay, stoppingToken);
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
    /// Executes one full sync cycle. Company first (parent), then Pixel (child FK).
    /// </summary>
    public async Task RunSyncAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Guard: verify target tables exist
        await using (var guardConn = new SqlConnection(_settings.ConnectionString))
        {
            await guardConn.OpenAsync(ct);
            if (!await GuardTablesExistAsync(guardConn, ct))
                return;
        }

        // Sync Company
        var coSyncId = await InsertSyncLogAsync("Company", ct);
        int coIns = 0, coUpd = 0, coDel = 0;
        try
        {
            var coStart = ForgeMetrics.StartTimer();
            (coIns, coUpd, coDel) = await SyncCompanyAsync(ct);
            _metrics.RecordCompanySync(coStart, coIns, coUpd, coDel);
            _metrics.RecordCompanySyncRun();
            if (coSyncId > 0)
                await CompleteSyncLogAsync(coSyncId, coIns, coUpd, coDel, null, ct);
        }
        catch (Exception ex)
        {
            _metrics.RecordSyncFailure();
            _metrics.RecordSyncLifetimeFailure();
            if (coSyncId > 0)
                await CompleteSyncLogAsync(coSyncId, coIns, coUpd, coDel, ex.Message, ct);
            throw;
        }

        // Sync Pixel
        var pxSyncId = await InsertSyncLogAsync("Pixel", ct);
        int pxIns = 0, pxUpd = 0, pxDel = 0;
        try
        {
            var pxStart = ForgeMetrics.StartTimer();
            (pxIns, pxUpd, pxDel) = await SyncPixelAsync(ct);
            _metrics.RecordPixelSync(pxStart, pxIns, pxUpd, pxDel);
            _metrics.RecordPixelSyncRun();
            if (pxSyncId > 0)
                await CompleteSyncLogAsync(pxSyncId, pxIns, pxUpd, pxDel, null, ct);
        }
        catch (Exception ex)
        {
            _metrics.RecordSyncFailure();
            _metrics.RecordSyncLifetimeFailure();
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

    private async Task<int> InsertSyncLogAsync(string syncType, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO IPInfo.ImportLog (SyncType)
                OUTPUT INSERTED.ImportId
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

    private async Task CompleteSyncLogAsync(int syncLogId, int rowsInserted, int rowsUpdated,
        int rowsDeleted, string? errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE IPInfo.ImportLog SET
                    CompletedAt     = SYSUTCDATETIME(),
                    RowsImported    = @RowsInserted,
                    RowsUpdated     = @RowsUpdated,
                    RowsDeleted     = @RowsDeleted,
                    DurationMs      = DATEDIFF(MILLISECOND, StartedAt, SYSUTCDATETIME()),
                    ErrorMessage    = @ErrorMessage
                WHERE ImportId = @SyncId";
            cmd.Parameters.AddWithValue("@SyncId", syncLogId);
            cmd.Parameters.AddWithValue("@RowsInserted", rowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", rowsUpdated);
            cmd.Parameters.AddWithValue("@RowsDeleted", rowsDeleted);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Non-critical
        }
    }

    // ========================================================================
    // COMPANY SYNC
    // ========================================================================

    private async Task<(int Inserted, int Updated, int Deleted)> SyncCompanyAsync(CancellationToken ct)
    {
        await using var localConn = new SqlConnection(_settings.ConnectionString);
        await localConn.OpenAsync(ct);

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

        var (inserted, updated, deleted) = await WithDeadlockRetryAsync(async () =>
        {
            int ins = 0, upd = 0, del = 0;
            await using var cmd = localConn.CreateCommand();
            cmd.CommandText = @"
                SET IDENTITY_INSERT PiXL.Company ON;

                -- UPDATE existing rows
                UPDATE t SET
                    t.CompanyName          = s.CompanyName,
                    t.ContactName          = s.ContactName,
                    t.Email                = s.Email,
                    t.Phone                = s.Phone,
                    t.Address              = s.Address,
                    t.City                 = s.City,
                    t.State                = s.State,
                    t.Zipcode              = s.Zipcode,
                    t.CompanyTypeId        = s.CompanyTypeId,
                    t.TaxId                = s.TaxId,
                    t.Extension            = s.Extension,
                    t.Address2             = s.Address2,
                    t.ParentCompanyId      = s.ParentCompanyId,
                    t.OriginalParentCoId   = s.OriginalParentCoId,
                    t.BillingResponsibleFlag = s.BillingResponsibleFlag,
                    t.BillingResponsibleId = s.BillingResponsibleId,
                    t.NAICS_SIC            = s.NAICS_SIC,
                    t.NAICS_Code           = s.NAICS_Code,
                    t.SIC_Code             = s.SIC_Code,
                    t.PortalURL            = s.PortalURL,
                    t.Profit               = s.Profit,
                    t.Cost                 = s.Cost,
                    t.P_Cost               = s.P_Cost,
                    t.P_Margin             = s.P_Margin,
                    t.SuspensionDate       = s.SuspensionDate,
                    t.Reasons              = s.Reasons,
                    t.StatusLastUpdated    = s.StatusLastUpdated,
                    t.PriceListExempt      = s.PriceListExempt,
                    t.BillingExempt        = s.BillingExempt,
                    t.SalesRepresentative  = s.SalesRepresentative,
                    t.RampUpPeriod         = s.RampUpPeriod,
                    t.RampUpDate           = s.RampUpDate,
                    t.MinOrder             = s.MinOrder,
                    t.StatusId             = s.StatusId,
                    t.IsActive             = s.IsActive,
                    t.CreatedDate          = s.CreatedDate,
                    t.ModifiedDate         = s.ModifiedDate
                FROM PiXL.Company t
                INNER JOIN #CompanyStaging s ON t.CompanyID = s.CompanyID;

                SET @upd = @@ROWCOUNT;

                -- INSERT new rows
                INSERT INTO PiXL.Company (
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
                SELECT
                    s.CompanyID, s.CompanyName, s.ContactName, s.Email, s.Phone,
                    s.Address, s.City, s.State, s.Zipcode, s.CompanyTypeId, s.TaxId,
                    s.Extension, s.Address2, s.ParentCompanyId, s.OriginalParentCoId,
                    s.BillingResponsibleFlag, s.BillingResponsibleId,
                    s.NAICS_SIC, s.NAICS_Code, s.SIC_Code, s.PortalURL,
                    s.Profit, s.Cost, s.P_Cost, s.P_Margin,
                    s.SuspensionDate, s.Reasons, s.StatusLastUpdated,
                    s.PriceListExempt, s.BillingExempt, s.SalesRepresentative,
                    s.RampUpPeriod, s.RampUpDate, s.MinOrder,
                    s.StatusId, s.IsActive, s.CreatedDate, s.ModifiedDate
                FROM #CompanyStaging s
                WHERE NOT EXISTS (SELECT 1 FROM PiXL.Company t WHERE t.CompanyID = s.CompanyID);

                SET @ins = @@ROWCOUNT;

                -- DELETE rows no longer in source (protect demo company 12344)
                DELETE t
                FROM PiXL.Company t
                WHERE t.CompanyID <> 12344
                  AND NOT EXISTS (SELECT 1 FROM #CompanyStaging s WHERE s.CompanyID = t.CompanyID);

                SET @del = @@ROWCOUNT;

                SET IDENTITY_INSERT PiXL.Company OFF;

                SELECT @ins, @upd, @del;

                DROP TABLE #CompanyStaging;";
            cmd.Parameters.Add(new SqlParameter("@ins", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output });
            cmd.Parameters.Add(new SqlParameter("@upd", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output });
            cmd.Parameters.Add(new SqlParameter("@del", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output });
            cmd.CommandTimeout = 120;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                ins = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                upd = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                del = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
            return (ins, upd, del);
        }, "Company UPDATE+INSERT");

        return (inserted, updated, deleted);
    }

    // ========================================================================
    // PIXEL SYNC
    // ========================================================================

    private async Task<(int Inserted, int Updated, int Deleted)> SyncPixelAsync(CancellationToken ct)
    {
        await using var localConn = new SqlConnection(_settings.ConnectionString);
        await localConn.OpenAsync(ct);

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

        var (inserted, updated, deleted) = await WithDeadlockRetryAsync(async () =>
        {
            int ins = 0, upd = 0, del = 0;
            await using var syncCmd = localConn.CreateCommand();
            syncCmd.CommandText = @"
                DECLARE @ins INT, @upd INT, @del INT;

                -- UPDATE existing pixels
                UPDATE t SET
                    t.PiXLName         = s.PiXLName,
                    t.SmartPiXL        = s.SmartPiXL,
                    t.PiXLNew          = s.PiXLNew,
                    t.PiXLLegacy       = s.PiXLLegacy,
                    t.OutputPathNew    = s.OutputPathNew,
                    t.OutputPathLegacy = s.OutputPathLegacy,
                    t.PiXLContactName  = s.PiXLContactName,
                    t.PiXLContactEmail = s.PiXLContactEmail,
                    t.PiXLURL          = s.PiXLURL,
                    t.Alertnotraffic   = s.Alertnotraffic,
                    t.TimeAlert        = s.TimeAlert,
                    t.Zipcode          = s.Zipcode,
                    t.Radius           = s.Radius,
                    t.Nationwide       = s.Nationwide,
                    t.NumberPage       = s.NumberPage,
                    t.TimeSite         = s.TimeSite,
                    t.IncomeRefInitial = s.IncomeRefInitial,
                    t.IncomeRefFinal   = s.IncomeRefFinal,
                    t.InferredCS       = s.InferredCS,
                    t.NetWorth         = s.NetWorth,
                    t.Married_Y        = s.Married_Y,
                    t.Married_N        = s.Married_N,
                    t.Married_U        = s.Married_U,
                    t.Children_Y       = s.Children_Y,
                    t.Children_N       = s.Children_N,
                    t.Children_U       = s.Children_U,
                    t.Gender_F         = s.Gender_F,
                    t.Gender_M         = s.Gender_M,
                    t.Gender_U         = s.Gender_U,
                    t.UserId           = s.UserId,
                    t.StatusId         = s.StatusId,
                    t.SuspendedId      = s.SuspendedId,
                    t.Reasons          = s.Reasons,
                    t.CreatedDate      = s.CreatedDate,
                    t.StatusLastUpdated = s.StatusLastUpdated,
                    t.PiXLAddress      = s.PiXLAddress,
                    t.PiXLCity         = s.PiXLCity,
                    t.PiXLState        = s.PiXLState,
                    t.PiXLZipCode      = s.PiXLZipCode,
                    t.PiXLPolicyURL    = s.PiXLPolicyURL,
                    t.PiXLDomain       = s.PiXLDomain,
                    t.PiXLLatitude     = s.PiXLLatitude,
                    t.PiXLLongitude    = s.PiXLLongitude,
                    t.[PiXL 2.5]       = s.[PiXL 2.5]
                FROM PiXL.Settings t
                INNER JOIN #PixelStaging s
                    ON t.CompanyId = s.CompanyId AND t.PiXLId = s.PiXLId;
                SET @upd = @@ROWCOUNT;

                -- INSERT new pixels
                INSERT INTO PiXL.Settings (
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
                SELECT
                    s.CompanyId, s.PiXLId, s.PiXLName, s.SmartPiXL, s.PiXLNew,
                    s.PiXLLegacy, s.OutputPathNew, s.OutputPathLegacy,
                    s.PiXLContactName, s.PiXLContactEmail, s.PiXLURL,
                    s.Alertnotraffic, s.TimeAlert, s.Zipcode, s.Radius,
                    s.Nationwide, s.NumberPage, s.TimeSite,
                    s.IncomeRefInitial, s.IncomeRefFinal, s.InferredCS, s.NetWorth,
                    s.Married_Y, s.Married_N, s.Married_U,
                    s.Children_Y, s.Children_N, s.Children_U,
                    s.Gender_F, s.Gender_M, s.Gender_U,
                    s.UserId, s.StatusId, s.SuspendedId, s.Reasons,
                    s.CreatedDate, s.StatusLastUpdated,
                    s.PiXLAddress, s.PiXLCity, s.PiXLState, s.PiXLZipCode,
                    s.PiXLPolicyURL, s.PiXLDomain,
                    s.PiXLLatitude, s.PiXLLongitude, s.[PiXL 2.5]
                FROM #PixelStaging s
                WHERE NOT EXISTS (
                    SELECT 1 FROM PiXL.Settings t
                    WHERE t.CompanyId = s.CompanyId AND t.PiXLId = s.PiXLId);
                SET @ins = @@ROWCOUNT;

                -- DELETE removed pixels (protect demo pixel 12344)
                DELETE t FROM PiXL.Settings t
                WHERE t.CompanyId <> 12344
                  AND NOT EXISTS (
                    SELECT 1 FROM #PixelStaging s
                    WHERE s.CompanyId = t.CompanyId AND s.PiXLId = t.PiXLId);
                SET @del = @@ROWCOUNT;

                SELECT @ins, @upd, @del;

                DROP TABLE #PixelStaging;";
            syncCmd.CommandTimeout = 120;

            await using var reader = await syncCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                ins = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                upd = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                del = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
            return (ins, upd, del);
        }, "PiXL UPDATE+INSERT");

        return (inserted, updated, deleted);
    }
}
