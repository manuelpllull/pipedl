using Quartz;
using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public class SyncJob : IJob
{
    private readonly ILogger<SyncJob> _logger;
    private readonly ISyncJobQueue _queue;
    private readonly RuntimeSettings _runtimeSettings;

    public SyncJob(ILogger<SyncJob> logger, ISyncJobQueue queue, RuntimeSettings runtimeSettings)
    {
        _logger = logger;
        _queue = queue;
        _runtimeSettings = runtimeSettings;
    }

    public Task Execute(IJobExecutionContext context)
    {
        var settings = _runtimeSettings.ResolveSyncSettings();

        _logger.LogInformation(
            "Running sync job for user {UserId} (downloads: {DownloadTracks}, targetPlaylistCount: {TargetPlaylistCount})",
            settings.UserId,
            settings.DownloadTracks,
            settings.TargetPlaylistCount?.ToString() ?? "<all>");

        var accepted = _queue.Enqueue(settings, "scheduler");
        _logger.LogInformation("Scheduled sync was queued with JobId={JobId}", accepted.JobId);

        return Task.CompletedTask;
    }
}
