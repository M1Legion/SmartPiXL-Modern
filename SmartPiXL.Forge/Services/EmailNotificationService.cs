using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// EMAIL + SMS NOTIFICATION SERVICE — Lightweight SMTP sender for ops alerts.
//
// Ported from SmartPiXL.Worker-Deprecated/Services/EmailNotificationService.cs
// with namespace updated to SmartPiXL.Forge.Services.
//
// CHANNELS:
//   Email  — full-detail HTML/text body, rate-limited 1/issue-type/hour
//   SMS    — ≤160-char summary via carrier email-to-SMS gateway (e.g.,
//            Verizon @vtext.com), rate-limited 1/issue-type/2 hours
//
//   Call NotifyAsync to fire both channels in parallel. Each channel
//   rate-limits and fails independently — one channel failing never
//   blocks the other.
//
// CONFIGURATION:
//   Email: SmtpHost, SmtpPort, SmtpUsername, SmtpPassword, SmtpFromAddress,
//          OpsNotificationEmail
//   SMS:   SmsGatewayAddress (carrier gateway email — same SMTP transport)
//   If SmtpHost is null/empty, both channels are disabled.
// ============================================================================

/// <summary>
/// Singleton service that sends operational alerts via email and SMS.
/// <para>
/// All sends are fire-and-forget with error swallowing — notification delivery
/// must never crash the host process or block the self-healing loop.
/// </para>
/// </summary>
public sealed class EmailNotificationService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    // Rate limiting: track last send time per issue type per channel
    private readonly ConcurrentDictionary<string, DateTime> _lastEmailSent = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSmsSent = new();
    private static readonly TimeSpan EmailRateLimit = TimeSpan.FromHours(1);
    private static readonly TimeSpan SmsRateLimit = TimeSpan.FromHours(2);

    /// <summary>Max SMS body length. Carrier gateways silently truncate or split beyond 160.</summary>
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
    /// Returns (emailSent, smsSent) indicating which channels fired.
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
    /// Returns true if the email was sent, false if skipped (not configured, rate-limited, or error).
    /// </summary>
    public async Task<bool> TrySendAsync(string issueType, string subject, string body)
    {
        if (!IsConfigured) return false;

        // Rate limit: skip if we already sent for this issue type within the hour
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
            // Email failure must never crash the service
            _logger.Warning($"Failed to send ops email for {issueType}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends an SMS notification via carrier email-to-SMS gateway.
    /// Rate-limited to 1 per issue type per 2 hours (texts are more intrusive).
    /// The message is truncated to 160 characters for carrier compatibility.
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
            // Carrier gateways deliver the email body as the text message.
            // Subject is often ignored or prepended — keep the body self-contained.
            var smsBody = $"PiXL: {subject}";
            if (smsBody.Length > SmsMaxLength)
                smsBody = string.Concat(smsBody.AsSpan(0, SmsMaxLength - 1), "\u2026");

            using var client = CreateSmtpClient();
            using var msg = new MailMessage(
                _settings.SmtpFromAddress,
                _settings.SmsGatewayAddress!,
                "", // Subject blank — some carriers show it, some don't
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

    /// <summary>Creates a configured SmtpClient from settings. Caller must dispose.</summary>
    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient(_settings.SmtpHost!, _settings.SmtpPort)
        {
            EnableSsl = _settings.SmtpEnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 15_000 // 15s timeout — don't block the healing loop
        };

        if (!string.IsNullOrEmpty(_settings.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(
                _settings.SmtpUsername, _settings.SmtpPassword);
        }

        return client;
    }
}
