using Quartz;
using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public class SyncJob : IJob
{
    private readonly ILogger<SyncJob> _logger;
    private readonly SyncPipeline _pipeline;
    private readonly RuntimeSettings _runtimeSettings;

    public SyncJob(ILogger<SyncJob> logger, SyncPipeline pipeline, RuntimeSettings runtimeSettings)
    {
        _logger = logger;
        _pipeline = pipeline;
        _runtimeSettings = runtimeSettings;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var settings = _runtimeSettings.ResolveSyncSettings();

        _logger.LogInformation(
            "Running sync job for user {UserId} (downloads: {DownloadTracks}, targetPlaylistCount: {TargetPlaylistCount})",
            settings.UserId,
            settings.DownloadTracks,
            settings.TargetPlaylistCount?.ToString() ?? "<all>");

        var result = await _pipeline.RunAsync(
            settings.UserId,
            settings.OutputDir,
            settings.DownloadTracks,
            settings.TargetPlaylistCount,
            context.CancellationToken);

        _logger.LogInformation(
            "Sync finished. Playlists={Playlists}, ScrapedTracks={ScrapedTracks}, Pending={Pending}, Downloaded={Downloaded}, Failed={Failed}",
            result.Playlists,
            result.ScrapedTracks,
            result.PendingBeforeDownload,
            result.Downloaded,
            result.FailedDownloads);
    }
}
