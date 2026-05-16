using Quartz;
using Microsoft.Extensions.Logging;

namespace Pipedl.Worker;

public class SyncJob : IJob
{
    private readonly ILogger<SyncJob> _logger;

    public SyncJob(ILogger<SyncJob> logger)
    {
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Running sync job...");
        return Task.CompletedTask;
    }
}
