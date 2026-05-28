using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Pipedl.Worker;

public sealed class BrevoEmailOptions
{
    public const string SectionName = "BrevoEmail";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class BrevoEmailNotificationPublisher : ISyncNotificationPublisher
{
    private readonly BrevoEmailOptions _options;
    private readonly ILogger<BrevoEmailNotificationPublisher> _logger;

    public BrevoEmailNotificationPublisher(BrevoEmailOptions options, ILogger<BrevoEmailNotificationPublisher> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task PublishAsync(SyncJobNotification notification, CancellationToken cancellationToken)
    {
        var message = BuildMessage(notification);

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("Brevo email notification sent for job {JobId} ({State})", notification.JobId, notification.State);
    }

    private MimeMessage BuildMessage(SyncJobNotification notification)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_options.From));
        msg.To.Add(MailboxAddress.Parse(_options.To));

        var stateText = notification.State.ToString().ToUpperInvariant();
        msg.Subject = $"[pipedl] Sync {stateText} - {notification.Settings.UserId}";

        var body = notification.State switch
        {
            SyncJobState.Succeeded =>
                $"""
                Sync job completed successfully.

                JobId: {notification.JobId}
                RequestedBy: {notification.RequestedBy}
                UserId: {notification.Settings.UserId}
                OutputDir: {notification.Settings.OutputDir}
                DownloadTracks: {notification.Settings.DownloadTracks}
                TargetPlaylistCount: {notification.Settings.TargetPlaylistCount?.ToString() ?? "<all>"}
                CompletedAtUtc: {notification.CompletedAtUtc:O}

                Result:
                - Playlists: {notification.Result?.Playlists}
                - ScrapedTracks: {notification.Result?.ScrapedTracks}
                - PendingBeforeDownload: {notification.Result?.PendingBeforeDownload}
                - Downloaded: {notification.Result?.Downloaded}
                - FailedDownloads: {notification.Result?.FailedDownloads}
                - SkippedDownloads: {notification.Result?.SkippedDownloads}
                - OutputDirectory: {notification.Result?.OutputDirectory}
                """,

            SyncJobState.Failed =>
                $"""
                Sync job failed.

                JobId: {notification.JobId}
                RequestedBy: {notification.RequestedBy}
                UserId: {notification.Settings.UserId}
                OutputDir: {notification.Settings.OutputDir}
                DownloadTracks: {notification.Settings.DownloadTracks}
                TargetPlaylistCount: {notification.Settings.TargetPlaylistCount?.ToString() ?? "<all>"}
                CompletedAtUtc: {notification.CompletedAtUtc:O}

                Error:
                {notification.Error}
                """,

            _ =>
                $"Sync job state changed to {notification.State} for job {notification.JobId}."
        };

        msg.Body = new TextPart("plain") { Text = body.Trim() };
        return msg;
    }
}
