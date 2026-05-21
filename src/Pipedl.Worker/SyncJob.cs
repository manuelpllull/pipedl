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
        var targetPlaylist = Environment.GetEnvironmentVariable("TARGET_PLAYLIST");

        _logger.LogInformation(
            "Running sync job for user {UserId} (downloads: {DownloadTracks}, targetPlaylist: {TargetPlaylist})",
            userId,
            downloadTracks,
            targetPlaylist ?? "<all>");

        var result = await _pipeline.RunAsync(userId, outputDir, downloadTracks, targetPlaylist, context.CancellationToken);

        _logger.LogInformation(
            "Sync finished. Playlists={Playlists}, ScrapedTracks={ScrapedTracks}, Pending={Pending}, Downloaded={Downloaded}, Failed={Failed}",
            result.Playlists,
            result.ScrapedTracks,
            result.PendingBeforeDownload,
            result.Downloaded,
            result.FailedDownloads);
    }
}
