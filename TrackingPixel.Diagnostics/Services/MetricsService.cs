using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using TrackingPixel.Diagnostics.Models;

namespace TrackingPixel.Diagnostics.Services;

public class MetricsService(SqlConnection db, ILogger<MetricsService> logger)
{
    public async Task<SummaryStats> GetSummaryStatsAsync()
    {
        try
        {
            await db.OpenAsync();
            
            var stats = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(*) AS TotalHits,
                    COUNT(DISTINCT CompositeFingerprint) AS UniqueDevices,
                    COUNT(DISTINCT IPAddress) AS UniqueIPs,
                    SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -24, GETUTCDATE()) THEN 1 ELSE 0 END) AS Last24HourHits,
                    SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE()) THEN 1 ELSE 0 END) AS LastHourHits
                FROM vw_PiXL_Summary");

            var botRate = await db.QueryFirstOrDefaultAsync<double?>(@"
                SELECT CAST(SUM(CASE WHEN BotRisk >= 50 THEN 1 ELSE 0 END) AS FLOAT) / NULLIF(COUNT(*), 0) * 100
                FROM vw_PiXL_Summary") ?? 0;

            var crossNetwork = await db.QueryFirstOrDefaultAsync<int?>(@"
                SELECT COUNT(*) FROM vw_PiXL_DeviceIdentity WHERE UniqueIPAddresses > 1") ?? 0;

            return new SummaryStats
            {
                TotalHits = (int)(stats?.TotalHits ?? 0),
                UniqueDevices = (int)(stats?.UniqueDevices ?? 0),
                UniqueIPs = (int)(stats?.UniqueIPs ?? 0),
                Last24HourHits = (int)(stats?.Last24HourHits ?? 0),
                LastHourHits = (int)(stats?.LastHourHits ?? 0),
                BotRate = Math.Round(botRate, 1),
                EvasionRate = 0,
                CrossNetworkDevices = crossNetwork
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSummaryStatsAsync: Failed to retrieve summary statistics");
            throw;
        }
    }

    public async Task<IEnumerable<HourlyStat>> GetHourlyStatsAsync()
    {
        try
        {
            await db.OpenAsync();
            return await db.QueryAsync<HourlyStat>(@"
                SELECT 
                    DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0) AS Hour,
                    COUNT(*) AS Hits,
                    COUNT(DISTINCT CompositeFingerprint) AS UniqueDevices
                FROM vw_PiXL_Summary
                WHERE ReceivedAt >= DATEADD(HOUR, -24, GETUTCDATE())
                GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0)
                ORDER BY Hour");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetHourlyStatsAsync: Failed to retrieve hourly statistics");
            throw;
        }
    }

    public async Task<IEnumerable<DeviceBreakdown>> GetDeviceBreakdownAsync()
    {
        // Try the aggregate view first, fall back to parsing DeviceProfile
        try
        {
            await db.OpenAsync();
            return await db.QueryAsync<DeviceBreakdown>(@"
                SELECT 
                    DeviceType, OS, Browser, Hits AS Count,
                    CAST(Hits AS FLOAT) / NULLIF(SUM(Hits) OVER(), 0) * 100 AS Percentage
                FROM vw_PiXL_DeviceBreakdown
                WHERE TrackingDate >= DATEADD(DAY, -7, CAST(GETUTCDATE() AS DATE))
                ORDER BY Hits DESC");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetDeviceBreakdownAsync: Primary query failed, attempting fallback");
            
            // Fallback: parse from Summary view
            try
            {
                return await db.QueryAsync<DeviceBreakdown>(@"
                    WITH Breakdown AS (
                        SELECT 
                            PARSENAME(REPLACE(DeviceProfile, '/', '.'), 3) AS DeviceType,
                            PARSENAME(REPLACE(DeviceProfile, '/', '.'), 2) AS OS,
                            PARSENAME(REPLACE(DeviceProfile, '/', '.'), 1) AS Browser,
                            COUNT(*) AS Count
                        FROM vw_PiXL_Summary
                        WHERE ReceivedAt >= DATEADD(DAY, -7, GETUTCDATE())
                        GROUP BY DeviceProfile
                    )
                    SELECT 
                        DeviceType, OS, Browser, Count,
                        CAST(Count AS FLOAT) / NULLIF(SUM(Count) OVER(), 0) * 100 AS Percentage
                    FROM Breakdown
                    ORDER BY Count DESC");
            }
            catch (Exception fallbackEx)
            {
                logger.LogError(fallbackEx, "GetDeviceBreakdownAsync: Fallback query also failed");
                return [];
            }
        }
    }

    public async Task<object> GetBotAnalysisAsync()
    {
        try
        {
            await db.OpenAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetBotAnalysisAsync: Failed to open database connection");
            return new { Distribution = Enumerable.Empty<BotAnalysis>(), Indicators = Enumerable.Empty<BotIndicator>() };
        }
        
        // Try aggregate view first
        IEnumerable<BotAnalysis> distribution;
        try
        {
            distribution = await db.QueryAsync<BotAnalysis>(@"
                SELECT 
                    RiskBucket,
                    Hits,
                    UniqueFingerprints AS UniqueDevices,
                    CAST(Hits AS FLOAT) / NULLIF(SUM(Hits) OVER(), 0) * 100 AS Percentage
                FROM vw_PiXL_BotAnalysis
                ORDER BY 
                    CASE RiskBucket 
                        WHEN 'High Risk (80-100)' THEN 1
                        WHEN 'Medium Risk (50-79)' THEN 2
                        WHEN 'Low Risk (20-49)' THEN 3
                        ELSE 4
                    END");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetBotAnalysisAsync: Primary distribution query failed, attempting fallback");
            
            // Fallback
            try
            {
                distribution = await db.QueryAsync<BotAnalysis>(@"
                    WITH Buckets AS (
                        SELECT 
                            CASE 
                                WHEN BotRisk >= 80 THEN 'High Risk (80-100)'
                                WHEN BotRisk >= 50 THEN 'Medium Risk (50-79)'
                                WHEN BotRisk >= 20 THEN 'Low Risk (20-49)'
                                ELSE 'Likely Human (0-19)'
                            END AS RiskBucket,
                            COUNT(*) AS Hits,
                            COUNT(DISTINCT CompositeFingerprint) AS UniqueDevices
                        FROM vw_PiXL_Summary
                        GROUP BY CASE 
                            WHEN BotRisk >= 80 THEN 'High Risk (80-100)'
                            WHEN BotRisk >= 50 THEN 'Medium Risk (50-79)'
                            WHEN BotRisk >= 20 THEN 'Low Risk (20-49)'
                            ELSE 'Likely Human (0-19)'
                        END
                    )
                    SELECT RiskBucket, Hits, UniqueDevices,
                           CAST(Hits AS FLOAT) / NULLIF(SUM(Hits) OVER(), 0) * 100 AS Percentage
                    FROM Buckets");
            }
            catch (Exception fallbackEx)
            {
                logger.LogError(fallbackEx, "GetBotAnalysisAsync: Fallback distribution query also failed");
                distribution = [];
            }
        }

        // Bot indicators from Complete view
        IEnumerable<BotIndicator> indicators;
        try
        {
            indicators = await db.QueryAsync<BotIndicator>(@"
                SELECT 'High Bot Score' AS Indicator, COUNT(*) AS Count 
                FROM vw_PiXL_Complete WHERE BotScore >= 50
                UNION ALL
                SELECT 'Canvas Evasion', COUNT(*) FROM vw_PiXL_Complete WHERE CanvasEvasionDetected = 1
                UNION ALL
                SELECT 'WebGL Evasion', COUNT(*) FROM vw_PiXL_Complete WHERE WebGLEvasionDetected = 1
                ORDER BY Count DESC");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetBotAnalysisAsync: Bot indicators query failed, returning empty list");
            indicators = [];
        }

        return new { Distribution = distribution, Indicators = indicators };
    }

    public async Task<FingerprintMetrics> GetFingerprintMetricsAsync()
    {
        try
        {
            await db.OpenAsync();
            
            // Use vw_PiXL_Complete for fingerprint components, vw_PiXL_Summary for composite
            var metrics = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(DISTINCT CanvasFingerprint) AS CanvasUnique,
                    COUNT(DISTINCT WebGLFingerprint) AS WebGLUnique,
                    COUNT(DISTINCT AudioFingerprintHash) AS AudioUnique,
                    COUNT(DISTINCT DetectedFonts) AS FontCombinations,
                    COUNT(DISTINCT CONCAT(ScreenWidth, 'x', ScreenHeight)) AS ScreenResolutions
                FROM vw_PiXL_Complete
                WHERE CanvasFingerprint IS NOT NULL");

            // Get total unique from Summary view which has CompositeFingerprint
            var summary = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(*) AS TotalHits,
                    COUNT(DISTINCT CompositeFingerprint) AS UniqueFingerprints
                FROM vw_PiXL_Summary");

            // Calculate collision rate
            var collisions = summary;

            double collisionRate = 0;
            if (collisions?.UniqueFingerprints > 0 && collisions?.TotalHits > 0)
            {
                collisionRate = (1.0 - ((double)collisions.UniqueFingerprints / (double)collisions.TotalHits)) * 100;
            }

            return new FingerprintMetrics
            {
                TotalUnique = (int)(summary?.UniqueFingerprints ?? 0),
                CollisionRate = Math.Round(collisionRate, 2),
                CanvasUnique = (int)(metrics?.CanvasUnique ?? 0),
                WebGLUnique = (int)(metrics?.WebGLUnique ?? 0),
                AudioUnique = (int)(metrics?.AudioUnique ?? 0),
                FontCombinations = (int)(metrics?.FontCombinations ?? 0),
                ScreenResolutions = (int)(metrics?.ScreenResolutions ?? 0)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFingerprintMetricsAsync: Failed to retrieve fingerprint metrics");
            throw;
        }
    }

    public async Task<IEnumerable<EvasionAttempt>> GetEvasionAttemptsAsync()
    {
        try
        {
            await db.OpenAsync();
            return await db.QueryAsync<EvasionAttempt>(@"
                SELECT 
                    CompositeFingerprint AS DeviceFingerprint,
                    SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END) AS CanvasVariations,
                    SUM(CASE WHEN WebGLEvasionDetected = 1 THEN 1 ELSE 0 END) AS WebGLVariations,
                    0.0 AS WebGLBlockedRate,
                    CASE 
                        WHEN SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END) > 0 THEN 'Canvas Evasion'
                        WHEN SUM(CASE WHEN WebGLEvasionDetected = 1 THEN 1 ELSE 0 END) > 0 THEN 'WebGL Evasion'
                        ELSE 'Unknown'
                    END AS EvasionType,
                    MAX(ReceivedAt) AS LastSeen
                FROM vw_PiXL_Complete
                WHERE CanvasEvasionDetected = 1 OR WebGLEvasionDetected = 1
                GROUP BY CompositeFingerprint
                ORDER BY LastSeen DESC");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetEvasionAttemptsAsync: Query failed, returning empty list");
            return [];
        }
    }

    public async Task<IEnumerable<CrossNetworkDevice>> GetCrossNetworkDevicesAsync()
    {
        try
        {
            await db.OpenAsync();
            return await db.QueryAsync<CrossNetworkDevice>(@"
                SELECT 
                    DeviceFingerprint,
                    DeviceType,
                    UniqueIPAddresses AS UniqueIPs,
                    TotalHits,
                    FirstSeen,
                    LastSeen,
                    '' AS IPList
                FROM vw_PiXL_DeviceIdentity
                WHERE UniqueIPAddresses > 1
                ORDER BY TotalHits DESC");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetCrossNetworkDevicesAsync: Query failed, returning empty list");
            return [];
        }
    }

    public async Task<IEnumerable<RecentActivity>> GetRecentActivityAsync(int count = 20)
    {
        try
        {
            await db.OpenAsync();
            return await db.QueryAsync<RecentActivity>(@"
                SELECT TOP (@count)
                    ReceivedAt AS Timestamp,
                    DeviceProfile,
                    IPAddress,
                    LocationProfile AS Location,
                    BotRisk,
                    CASE 
                        WHEN BotRisk >= 80 THEN 'HIGH'
                        WHEN BotRisk >= 50 THEN 'MEDIUM'
                        WHEN BotRisk >= 20 THEN 'LOW'
                        ELSE 'HUMAN'
                    END AS RiskLevel,
                    LEFT(CompositeFingerprint, 8) AS Fingerprint
                FROM vw_PiXL_Summary
                ORDER BY ReceivedAt DESC", new { count });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetRecentActivityAsync: Failed to retrieve recent activity");
            throw;
        }
    }
}
