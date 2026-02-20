using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// EMAIL + SMS NOTIFICATION SERVICE — Lightweight SMTP sender for ops alerts.
//
// Used by the /api/dash/test-notify endpoint to let the operator verify that
// email/SMS alerting is configured and working.
//
// CHANNELS:
//   Email — full-detail text body, rate-limited 1/issue-type/hour
//   SMS   — ≤160-char summary via carrier email-to-SMS gateway
//
// PORTED FROM: SmartPiXL.Worker-Deprecated/Services/EmailNotificationService.cs
// NAMESPACE:   SmartPiXL.Sentinel.Services (not TrackingPixel.Services)
// ============================================================================

/// <summary>
/// Singleton service that sends operational alerts via email and SMS.
/// All sends are fire-and-forget with error swallowing — notification delivery
/// must never crash the host process.
/// </summary>
public sealed class EmailNotificationService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    // Rate limiting: track last send time per issue type per channel
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastEmailSent = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSmsSent = new();
    private static readonly TimeSpan EmailRateLimit = TimeSpan.FromHours(1);
    private static readonly TimeSpan SmsRateLimit = TimeSpan.FromHours(2);

    private const int SmsMaxLength = 160;

    public EmailNotificationService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if SMTP is configured and an ops email address is set.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_settings.SmtpHost) &&
        !string.IsNullOrEmpty(_settings.OpsNotificationEmail);

    /// <summary>
    /// Returns true if SMS gateway is configured (requires SMTP host too).
    /// </summary>
    public bool IsSmsConfigured =>
        !string.IsNullOrEmpty(_settings.SmtpHost) &&
        !string.IsNullOrEmpty(_settings.SmsGatewayAddress);

    /// <summary>
    /// Sends both email and SMS notifications in parallel. Each channel
    /// rate-limits independently and failures don't block the other.
    /// </summary>
    public async Task<(bool EmailSent, bool SmsSent)> NotifyAsync(
        string issueType, string subject, string body)
    {
        var emailTask = TrySendAsync(issueType, subject, body);
        var smsTask = TrySendSmsAsync(issueType, subject);

        await Task.WhenAll(emailTask, smsTask);

        return (emailTask.Result, smsTask.Result);
    }

    /// <summary>
    /// Sends an ops notification email. Rate-limited to 1 per issue type per hour.
    /// </summary>
    public async Task<bool> TrySendAsync(string issueType, string subject, string body)
    {
        if (!IsConfigured) return false;

        var now = DateTime.UtcNow;
        if (_lastEmailSent.TryGetValue(issueType, out var lastTime) && now - lastTime < EmailRateLimit)
        {
            _logger.Debug($"Email rate-limited for {issueType} (last sent {(now - lastTime).TotalMinutes:F0}m ago)");
            return false;
        }

        try
        {
            using var client = CreateSmtpClient();
            using var msg = new MailMessage(
                _settings.SmtpFromAddress,
                _settings.OpsNotificationEmail!,
                $"[SmartPiXL Ops] {subject}",
                body)
            {
                IsBodyHtml = false
            };

            await client.SendMailAsync(msg);

            _lastEmailSent[issueType] = now;
            _logger.Info($"Sent ops email: {subject}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to send ops email for {issueType}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends an SMS notification via carrier email-to-SMS gateway.
    /// Rate-limited to 1 per issue type per 2 hours.
    /// </summary>
    public async Task<bool> TrySendSmsAsync(string issueType, string subject)
    {
        if (!IsSmsConfigured) return false;

        var now = DateTime.UtcNow;
        if (_lastSmsSent.TryGetValue(issueType, out var lastTime) && now - lastTime < SmsRateLimit)
        {
            _logger.Debug($"SMS rate-limited for {issueType} (last sent {(now - lastTime).TotalMinutes:F0}m ago)");
            return false;
        }

        try
        {
            var smsBody = $"PiXL: {subject}";
            if (smsBody.Length > SmsMaxLength)
                smsBody = string.Concat(smsBody.AsSpan(0, SmsMaxLength - 1), "…");

            using var client = CreateSmtpClient();
            using var msg = new MailMessage(
                _settings.SmtpFromAddress,
                _settings.SmsGatewayAddress!,
                "",
                smsBody)
            {
                IsBodyHtml = false
            };

            await client.SendMailAsync(msg);

            _lastSmsSent[issueType] = now;
            _logger.Info($"Sent SMS alert: {subject}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to send SMS for {issueType}: {ex.Message}");
            return false;
        }
    }

    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient(_settings.SmtpHost!, _settings.SmtpPort)
        {
            EnableSsl = _settings.SmtpEnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 15_000
        };

        if (!string.IsNullOrEmpty(_settings.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(
                _settings.SmtpUsername, _settings.SmtpPassword);
        }

        return client;
    }
}
