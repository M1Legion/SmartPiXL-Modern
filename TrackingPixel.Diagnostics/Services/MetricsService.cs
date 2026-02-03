using Dapper;
using Microsoft.Data.SqlClient;
using TrackingPixel.Diagnostics.Models;

namespace TrackingPixel.Diagnostics.Services;

public class MetricsService(SqlConnection db)
{
    public async Task<SummaryStats> GetSummaryStatsAsync()
    {
        await db.OpenAsync();
        
        var stats = await db.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT 
                COUNT(*) AS TotalHits,
                COUNT(DISTINCT DeviceFingerprint) AS UniqueDevices,
                COUNT(DISTINCT IPAddress) AS UniqueIPs,
                SUM(CASE WHEN CreatedAt >= DATEADD(HOUR, -24, GETUTCDATE()) THEN 1 ELSE 0 END) AS Last24HourHits,
                SUM(CASE WHEN CreatedAt >= DATEADD(HOUR, -1, GETUTCDATE()) THEN 1 ELSE 0 END) AS LastHourHits
            FROM vw_PiXL_Summary");

        var botRate = await db.QueryFirstOrDefaultAsync<double>(@"
            SELECT CAST(SUM(CASE WHEN BotRiskScore >= 50 THEN 1 ELSE 0 END) AS FLOAT) / NULLIF(COUNT(*), 0) * 100
            FROM vw_PiXL_Summary");

        var crossNetwork = await db.QueryFirstOrDefaultAsync<int>(@"
            SELECT COUNT(*) FROM vw_PiXL_DeviceIdentity WHERE UniqueIPAddresses > 1");

        var evasionRate = await db.QueryFirstOrDefaultAsync<double?>(@"
            SELECT CAST(COUNT(DISTINCT DeviceFingerprint) AS FLOAT) / 
                   NULLIF((SELECT COUNT(DISTINCT DeviceFingerprint) FROM vw_PiXL_Complete), 0) * 100
            FROM vw_PiXL_Complete
            GROUP BY DeviceFingerprint
            HAVING COUNT(DISTINCT CanvasHash) > 3 
                OR AVG(CASE WHEN WebGLRenderer = 'Unknown' OR WebGLRenderer IS NULL THEN 1.0 ELSE 0 END) > 0.5") ?? 0;

        return new SummaryStats
        {
            TotalHits = (int)(stats?.TotalHits ?? 0),
            UniqueDevices = (int)(stats?.UniqueDevices ?? 0),
            UniqueIPs = (int)(stats?.UniqueIPs ?? 0),
            Last24HourHits = (int)(stats?.Last24HourHits ?? 0),
            LastHourHits = (int)(stats?.LastHourHits ?? 0),
            BotRate = Math.Round(botRate, 1),
            EvasionRate = Math.Round(evasionRate, 2),
            CrossNetworkDevices = crossNetwork
        };
    }

    public async Task<IEnumerable<HourlyStat>> GetHourlyStatsAsync()
    {
        await db.OpenAsync();
        return await db.QueryAsync<HourlyStat>(@"
            SELECT 
                DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0) AS Hour,
                COUNT(*) AS Hits,
                COUNT(DISTINCT DeviceFingerprint) AS UniqueDevices
            FROM vw_PiXL_Summary
            WHERE CreatedAt >= DATEADD(HOUR, -24, GETUTCDATE())
            GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0)
            ORDER BY Hour");
    }

    public async Task<IEnumerable<DeviceBreakdown>> GetDeviceBreakdownAsync()
    {
        await db.OpenAsync();
        return await db.QueryAsync<DeviceBreakdown>(@"
            WITH Breakdown AS (
                SELECT 
                    PARSENAME(REPLACE(DeviceProfile, '/', '.'), 3) AS DeviceType,
                    PARSENAME(REPLACE(DeviceProfile, '/', '.'), 2) AS OS,
                    PARSENAME(REPLACE(DeviceProfile, '/', '.'), 1) AS Browser,
                    COUNT(*) AS Count
                FROM vw_PiXL_Summary
                WHERE CreatedAt >= DATEADD(DAY, -7, GETUTCDATE())
                GROUP BY DeviceProfile
            )
            SELECT 
                DeviceType, OS, Browser, Count,
                CAST(Count AS FLOAT) / SUM(Count) OVER() * 100 AS Percentage
            FROM Breakdown
            ORDER BY Count DESC");
    }

    public async Task<object> GetBotAnalysisAsync()
    {
        await db.OpenAsync();
        
        var distribution = await db.QueryAsync<BotAnalysis>(@"
            WITH Buckets AS (
                SELECT 
                    CASE 
                        WHEN BotRiskScore >= 80 THEN 'High Risk (80-100)'
                        WHEN BotRiskScore >= 50 THEN 'Medium Risk (50-79)'
                        WHEN BotRiskScore >= 20 THEN 'Low Risk (20-49)'
                        ELSE 'Likely Human (0-19)'
                    END AS RiskBucket,
                    COUNT(*) AS Hits,
                    COUNT(DISTINCT DeviceFingerprint) AS UniqueDevices
                FROM vw_PiXL_Summary
                GROUP BY CASE 
                    WHEN BotRiskScore >= 80 THEN 'High Risk (80-100)'
                    WHEN BotRiskScore >= 50 THEN 'Medium Risk (50-79)'
                    WHEN BotRiskScore >= 20 THEN 'Low Risk (20-49)'
                    ELSE 'Likely Human (0-19)'
                END
            )
            SELECT RiskBucket, Hits, UniqueDevices,
                   CAST(Hits AS FLOAT) / SUM(Hits) OVER() * 100 AS Percentage
            FROM Buckets
            ORDER BY 
                CASE RiskBucket 
                    WHEN 'High Risk (80-100)' THEN 1
                    WHEN 'Medium Risk (50-79)' THEN 2
                    WHEN 'Low Risk (20-49)' THEN 3
                    ELSE 4
                END");

        var indicators = await db.QueryAsync<BotIndicator>(@"
            SELECT 'WebDriver' AS Indicator, COUNT(*) AS Count FROM TrackingData WHERE IsWebDriver = 1
            UNION ALL
            SELECT 'Headless', COUNT(*) FROM TrackingData WHERE IsHeadless = 1
            UNION ALL
            SELECT 'Phantom', COUNT(*) FROM TrackingData WHERE HasPhantom = 1
            UNION ALL
            SELECT 'Selenium', COUNT(*) FROM TrackingData WHERE HasSelenium = 1
            UNION ALL
            SELECT 'Missing Chrome Object', COUNT(*) FROM TrackingData WHERE ChromeMissing = 1
            ORDER BY Count DESC");

        return new { Distribution = distribution, Indicators = indicators };
    }

    public async Task<FingerprintMetrics> GetFingerprintMetricsAsync()
    {
        await db.OpenAsync();
        
        var metrics = await db.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT 
                COUNT(DISTINCT DeviceFingerprint) AS TotalUnique,
                COUNT(DISTINCT CanvasHash) AS CanvasUnique,
                COUNT(DISTINCT WebGLHash) AS WebGLUnique,
                COUNT(DISTINCT AudioFingerprint) AS AudioUnique,
                COUNT(DISTINCT FontList) AS FontCombinations,
                COUNT(DISTINCT CONCAT(ScreenWidth, 'x', ScreenHeight)) AS ScreenResolutions
            FROM TrackingData
            WHERE CanvasHash IS NOT NULL");

        // Calculate collision rate
        var collisions = await db.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT 
                COUNT(*) AS TotalHits,
                COUNT(DISTINCT DeviceFingerprint) AS UniqueFingerprints
            FROM TrackingData");

        double collisionRate = 0;
        if (collisions?.UniqueFingerprints > 0)
        {
            collisionRate = (1.0 - ((double)collisions.UniqueFingerprints / (double)collisions.TotalHits)) * 100;
        }

        return new FingerprintMetrics
        {
            TotalUnique = (int)(metrics?.TotalUnique ?? 0),
            CollisionRate = Math.Round(collisionRate, 2),
            CanvasUnique = (int)(metrics?.CanvasUnique ?? 0),
            WebGLUnique = (int)(metrics?.WebGLUnique ?? 0),
            AudioUnique = (int)(metrics?.AudioUnique ?? 0),
            FontCombinations = (int)(metrics?.FontCombinations ?? 0),
            ScreenResolutions = (int)(metrics?.ScreenResolutions ?? 0)
        };
    }

    public async Task<IEnumerable<EvasionAttempt>> GetEvasionAttemptsAsync()
    {
        await db.OpenAsync();
        return await db.QueryAsync<EvasionAttempt>(@"
            SELECT 
                DeviceFingerprint,
                COUNT(DISTINCT CanvasHash) AS CanvasVariations,
                COUNT(DISTINCT WebGLHash) AS WebGLVariations,
                AVG(CASE WHEN WebGLRenderer = 'Unknown' OR WebGLRenderer IS NULL THEN 1.0 ELSE 0 END) AS WebGLBlockedRate,
                CASE 
                    WHEN COUNT(DISTINCT CanvasHash) > 3 THEN 'Canvas Noise'
                    WHEN AVG(CASE WHEN WebGLRenderer = 'Unknown' OR WebGLRenderer IS NULL THEN 1.0 ELSE 0 END) > 0.5 THEN 'WebGL Blocked'
                    WHEN ScreenWidth = 1000 AND ScreenHeight = 900 THEN 'Tor Browser'
                    ELSE 'Unknown'
                END AS EvasionType,
                MAX(CreatedAt) AS LastSeen
            FROM TrackingData
            GROUP BY DeviceFingerprint, ScreenWidth, ScreenHeight
            HAVING COUNT(DISTINCT CanvasHash) > 3 
                OR AVG(CASE WHEN WebGLRenderer = 'Unknown' OR WebGLRenderer IS NULL THEN 1.0 ELSE 0 END) > 0.5
            ORDER BY LastSeen DESC");
    }

    public async Task<IEnumerable<CrossNetworkDevice>> GetCrossNetworkDevicesAsync()
    {
        await db.OpenAsync();
        return await db.QueryAsync<CrossNetworkDevice>(@"
            SELECT 
                i.DeviceFingerprint,
                i.DeviceType,
                i.UniqueIPAddresses AS UniqueIPs,
                i.TotalHits,
                i.FirstSeen,
                i.LastSeen,
                STUFF((
                    SELECT ', ' + ExternalIP 
                    FROM vw_PiXL_DeviceNetworkHistory h 
                    WHERE h.DeviceFingerprint = i.DeviceFingerprint 
                    FOR XML PATH('')
                ), 1, 2, '') AS IPList
            FROM vw_PiXL_DeviceIdentity i
            WHERE i.UniqueIPAddresses > 1
            ORDER BY i.TotalHits DESC");
    }

    public async Task<IEnumerable<RecentActivity>> GetRecentActivityAsync(int count = 20)
    {
        await db.OpenAsync();
        return await db.QueryAsync<RecentActivity>(@"
            SELECT TOP (@count)
                CreatedAt AS Timestamp,
                DeviceProfile,
                IPAddress,
                Location,
                BotRiskScore AS BotRisk,
                CASE 
                    WHEN BotRiskScore >= 80 THEN 'HIGH'
                    WHEN BotRiskScore >= 50 THEN 'MEDIUM'
                    WHEN BotRiskScore >= 20 THEN 'LOW'
                    ELSE 'HUMAN'
                END AS RiskLevel,
                LEFT(DeviceFingerprint, 8) AS Fingerprint
            FROM vw_PiXL_Summary
            ORDER BY CreatedAt DESC", new { count });
    }
}
