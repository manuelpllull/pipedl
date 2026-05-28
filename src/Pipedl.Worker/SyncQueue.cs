using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public enum SyncJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed record SyncJobItem(
    Guid JobId,
    SyncExecutionSettings Settings,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record SyncJobStatusDto(
    Guid JobId,
    SyncJobState State,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    SyncExecutionSettings Settings,
    SyncRunResult? Result,
    string? Error);

public sealed record SyncJobAcceptedDto(
    Guid JobId,
    SyncJobState State,
    string StatusUrl,
    DateTimeOffset CreatedAtUtc);

public sealed record SyncJobNotification(
    Guid JobId,
    SyncJobState State,
    string RequestedBy,
    SyncExecutionSettings Settings,
    SyncRunResult? Result,
    string? Error,
    DateTimeOffset? CompletedAtUtc);

public interface ISyncNotificationPublisher
{
    Task PublishAsync(SyncJobNotification notification, CancellationToken cancellationToken);
}

public sealed class NoOpSyncNotificationPublisher : ISyncNotificationPublisher
{
    private readonly ILogger<NoOpSyncNotificationPublisher> _logger;

    public NoOpSyncNotificationPublisher(ILogger<NoOpSyncNotificationPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(SyncJobNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Notification placeholder for job {JobId} in state {State}. Plug an email publisher into ISyncNotificationPublisher.",
            notification.JobId,
            notification.State);
        return Task.CompletedTask;
    }
}

public interface ISyncJobQueue
{
    SyncJobAcceptedDto Enqueue(SyncExecutionSettings settings, string requestedBy = "api");
    bool TryGet(Guid jobId, out SyncJobStatusDto status);
    IAsyncEnumerable<SyncJobItem> DequeueAsync(CancellationToken cancellationToken);
    void MarkRunning(Guid jobId);
    void MarkSucceeded(Guid jobId, SyncRunResult result);
    void MarkFailed(Guid jobId, string error);
    void MarkCanceled(Guid jobId);
}

public sealed class InMemorySyncJobQueue : ISyncJobQueue
{
    private readonly Channel<SyncJobItem> _channel = Channel.CreateUnbounded<SyncJobItem>();
    private readonly ConcurrentDictionary<Guid, SyncJobStatusDto> _statuses = new();

    public SyncJobAcceptedDto Enqueue(SyncExecutionSettings settings, string requestedBy = "api")
    {
        var now = DateTimeOffset.UtcNow;
        var job = new SyncJobItem(Guid.NewGuid(), settings, requestedBy, now);

        var initial = new SyncJobStatusDto(
            job.JobId,
            SyncJobState.Queued,
            requestedBy,
            now,
            null,
            null,
            settings,
            null,
            null);

        _statuses[job.JobId] = initial;
        _channel.Writer.TryWrite(job);

        return new SyncJobAcceptedDto(job.JobId, SyncJobState.Queued, $"/api/sync/jobs/{job.JobId}", now);
    }

    public bool TryGet(Guid jobId, out SyncJobStatusDto status) => _statuses.TryGetValue(jobId, out status!);

    public IAsyncEnumerable<SyncJobItem> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void MarkRunning(Guid jobId)
    {
        if (_statuses.TryGetValue(jobId, out var current))
        {
            _statuses[jobId] = current with
            {
                State = SyncJobState.Running,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Error = null
            };
        }
    }

    public void MarkSucceeded(Guid jobId, SyncRunResult result)
    {
        if (_statuses.TryGetValue(jobId, out var current))
        {
            _statuses[jobId] = current with
            {
                State = SyncJobState.Succeeded,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Result = result,
                Error = null
            };
        }
    }

    public void MarkFailed(Guid jobId, string error)
    {
        if (_statuses.TryGetValue(jobId, out var current))
        {
            _statuses[jobId] = current with
            {
                State = SyncJobState.Failed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = error
            };
        }
    }

    public void MarkCanceled(Guid jobId)
    {
        if (_statuses.TryGetValue(jobId, out var current))
        {
            _statuses[jobId] = current with
            {
                State = SyncJobState.Canceled,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = "Canceled"
            };
        }
    }
}

public sealed class SyncJobProcessor : BackgroundService
{
    private readonly ISyncJobQueue _queue;
    private readonly ISyncExecutionService _executor;
    private readonly ISyncNotificationPublisher _notifications;
    private readonly ILogger<SyncJobProcessor> _logger;

    public SyncJobProcessor(
        ISyncJobQueue queue,
        ISyncExecutionService executor,
        ISyncNotificationPublisher notifications,
        ILogger<SyncJobProcessor> logger)
    {
        _queue = queue;
        _executor = executor;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.DequeueAsync(stoppingToken))
        {
            _queue.MarkRunning(job.JobId);
            try
            {
                var result = await _executor.ExecuteAsync(job.Settings, stoppingToken);
                _queue.MarkSucceeded(job.JobId, result);

                await _notifications.PublishAsync(
                    new SyncJobNotification(
                        job.JobId,
                        SyncJobState.Succeeded,
                        job.RequestedBy,
                        job.Settings,
                        result,
                        null,
                        DateTimeOffset.UtcNow),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _queue.MarkCanceled(job.JobId);
                _logger.LogWarning("Sync job {JobId} canceled.", job.JobId);
            }
            catch (Exception ex)
            {
                _queue.MarkFailed(job.JobId, ex.Message);
                _logger.LogError(ex, "Sync job {JobId} failed.", job.JobId);

                await _notifications.PublishAsync(
                    new SyncJobNotification(
                        job.JobId,
                        SyncJobState.Failed,
                        job.RequestedBy,
                        job.Settings,
                        null,
                        ex.Message,
                        DateTimeOffset.UtcNow),
                    stoppingToken);
            }
        }
    }
}
