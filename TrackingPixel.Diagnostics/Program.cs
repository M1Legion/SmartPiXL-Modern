using Microsoft.Data.SqlClient;
using TrackingPixel.Diagnostics.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind to localhost only - internal use
builder.WebHost.UseUrls("http://localhost:5050");

// Add services
builder.Services.AddRazorPages();
builder.Services.AddScoped<MetricsService>();
builder.Services.AddSingleton<HealthTracker>();

// SQL connection factory
builder.Services.AddScoped(_ => 
    new SqlConnection(builder.Configuration.GetConnectionString("SmartPixl")));

var app = builder.Build();

// Optional: API key authentication for remote access
var adminKey = builder.Configuration["AdminKey"];
if (!string.IsNullOrEmpty(adminKey) && adminKey != "change-this-secret-key")
{
    app.Use(async (context, next) =>
    {
        // Allow static files and pages without auth, require for API
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.Request.Headers["X-Admin-Key"] != adminKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
        await next();
    });
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// === API Endpoints ===

app.MapGet("/api/stats/summary", async (MetricsService metrics) =>
    await metrics.GetSummaryStatsAsync());

app.MapGet("/api/stats/hourly", async (MetricsService metrics) =>
    await metrics.GetHourlyStatsAsync());

app.MapGet("/api/stats/devices", async (MetricsService metrics) =>
    await metrics.GetDeviceBreakdownAsync());

app.MapGet("/api/stats/bots", async (MetricsService metrics) =>
    await metrics.GetBotAnalysisAsync());

app.MapGet("/api/stats/fingerprints", async (MetricsService metrics) =>
    await metrics.GetFingerprintMetricsAsync());

app.MapGet("/api/stats/evasion", async (MetricsService metrics) =>
    await metrics.GetEvasionAttemptsAsync());

app.MapGet("/api/stats/identity", async (MetricsService metrics) =>
    await metrics.GetCrossNetworkDevicesAsync());

app.MapGet("/api/activity/recent", async (MetricsService metrics, int? count) =>
    await metrics.GetRecentActivityAsync(count ?? 20));

app.MapGet("/api/health", (HealthTracker health) => health.GetStatus());

// Health tracking middleware
app.Use(async (context, next) =>
{
    var health = context.RequestServices.GetRequiredService<HealthTracker>();
    health.RecordRequest();
    await next();
});

app.Run();
