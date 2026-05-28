using Quartz;
using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public class SyncJob : IJob
{
    private readonly ILogger<SyncJob> _logger;
    private readonly SyncPipeline _pipeline;

    public SyncJob(ILogger<SyncJob> logger, SyncPipeline pipeline)
    {
        _logger = logger;
        _pipeline = pipeline;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var userId = Environment.GetEnvironmentVariable("TARGET_USER_ID") ?? "mnupea";
        var outputDir = Environment.GetEnvironmentVariable("MUSIC_OUTPUT_PATH") ?? "music";
        var downloadTracks = (Environment.GetEnvironmentVariable("DOWNLOAD_TRACKS") ?? "1") != "0";
        int? targetPlaylistCount = int.TryParse(Environment.GetEnvironmentVariable("TARGET_PLAYLIST_COUNT"), out var parsedCount) && parsedCount > 0
            ? parsedCount
            : null;

        _logger.LogInformation(
            "Running sync job for user {UserId} (downloads: {DownloadTracks}, targetPlaylistCount: {TargetPlaylistCount})",
            userId,
            downloadTracks,
            targetPlaylistCount?.ToString() ?? "<all>");

        var result = await _pipeline.RunAsync(userId, outputDir, downloadTracks, targetPlaylistCount, context.CancellationToken);

        _logger.LogInformation(
            "Sync finished. Playlists={Playlists}, ScrapedTracks={ScrapedTracks}, Pending={Pending}, Downloaded={Downloaded}, Failed={Failed}",
            result.Playlists,
            result.ScrapedTracks,
            result.PendingBeforeDownload,
            result.Downloaded,
            result.FailedDownloads);
    }
}
