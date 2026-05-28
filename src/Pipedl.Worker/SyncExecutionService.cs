using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public interface ISyncExecutionService
{
    Task<SyncRunResult> ExecuteAsync(SyncExecutionSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SyncExecutionService : ISyncExecutionService
{
    private readonly SyncPipeline _pipeline;
    private readonly ILogger<SyncExecutionService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public SyncExecutionService(SyncPipeline pipeline, ILogger<SyncExecutionService> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<SyncRunResult> ExecuteAsync(SyncExecutionSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.UserId))
            throw new ArgumentException("UserId is required.", nameof(settings));

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation(
                "Starting sync execution for user {UserId} (downloads: {DownloadTracks}, targetPlaylistCount: {TargetPlaylistCount})",
                settings.UserId,
                settings.DownloadTracks,
                settings.TargetPlaylistCount?.ToString() ?? "<all>");

            return await _pipeline.RunAsync(
                settings.UserId,
                settings.OutputDir,
                settings.DownloadTracks,
                settings.TargetPlaylistCount,
                cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }
}

public sealed record ForceSyncRequest(
    string? UserId,
    string? OutputDir,
    bool? DownloadTracks,
    int? TargetPlaylistCount);
