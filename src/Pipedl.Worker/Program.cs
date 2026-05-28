using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Pipedl.Infrastructure;
using Pipedl.Worker;
using System.Text.Json.Serialization;

async Task<int> MainAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    builder.Services
        .AddSingleton<RuntimeSettings>()
        .AddSingleton<DbConnectionFactory>(sp =>
        {
            var runtimeSettings = sp.GetRequiredService<RuntimeSettings>();
            return new DbConnectionFactory(runtimeSettings.ResolveConnectionString());
        })
        .AddSingleton<PlaylistScraper>()
        .AddSingleton<SyncPipeline>()
        .AddSingleton<ISyncExecutionService, SyncExecutionService>()
        .AddSingleton<ISyncJobQueue, InMemorySyncJobQueue>()
        .AddHostedService<SyncJobProcessor>();

    var brevo = builder.Configuration.GetSection(BrevoEmailOptions.SectionName).Get<BrevoEmailOptions>()
        ?? new BrevoEmailOptions();

    if (brevo.Enabled)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(brevo.Host)) missing.Add("BrevoEmail:Host");
        if (brevo.Port <= 0) missing.Add("BrevoEmail:Port");
        if (string.IsNullOrWhiteSpace(brevo.Username)) missing.Add("BrevoEmail:Username");
        if (string.IsNullOrWhiteSpace(brevo.Password)) missing.Add("BrevoEmail:Password");
        if (string.IsNullOrWhiteSpace(brevo.From)) missing.Add("BrevoEmail:From");
        if (string.IsNullOrWhiteSpace(brevo.To)) missing.Add("BrevoEmail:To");

        if (missing.Count > 0)
            throw new InvalidOperationException($"Brevo email is enabled but missing required config values: {string.Join(", ", missing)}");

        builder.Services.AddSingleton(brevo);
        builder.Services.AddSingleton<ISyncNotificationPublisher, BrevoEmailNotificationPublisher>();
    }
    else
    {
        builder.Services.AddSingleton<ISyncNotificationPublisher, NoOpSyncNotificationPublisher>();
    }

    builder.Services.AddQuartz(q =>
    {
        var jobKey = new JobKey("SyncJob");
        q.AddJob<SyncJob>(opts => opts.WithIdentity(jobKey));

        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity("SyncJob-trigger")
            .WithCronSchedule("0 0 * * * ?"));
    });
    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    var app = builder.Build();

    var runtime = app.Services.GetRequiredService<RuntimeSettings>();
    if (!runtime.IsRunOnceEnabled())
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/api/sync/run", async (
            ForceSyncRequest? request,
            RuntimeSettings settings,
            ISyncJobQueue queue,
            CancellationToken cancellationToken) =>
        {
            var defaults = settings.ResolveSyncSettings();
            var effective = defaults with
            {
                UserId = string.IsNullOrWhiteSpace(request?.UserId) ? defaults.UserId : request!.UserId!,
                OutputDir = string.IsNullOrWhiteSpace(request?.OutputDir) ? defaults.OutputDir : request!.OutputDir!,
                DownloadTracks = request?.DownloadTracks ?? defaults.DownloadTracks,
                TargetPlaylistCount = request?.TargetPlaylistCount ?? defaults.TargetPlaylistCount
            };

            if (string.IsNullOrWhiteSpace(effective.UserId))
                return Results.BadRequest(new { error = "UserId could not be resolved." });

            var accepted = queue.Enqueue(effective, "api");
            return Results.Accepted(accepted.StatusUrl, accepted);
        });

        app.MapGet("/api/sync/jobs/{jobId:guid}", (Guid jobId, ISyncJobQueue queue) =>
        {
            return queue.TryGet(jobId, out var status)
                ? Results.Ok(status)
                : Results.NotFound(new { error = "Job not found." });
        });

        await app.RunAsync();
        return 0;
    }

    var syncSettings = runtime.ResolveSyncSettings(args);
    if (string.IsNullOrWhiteSpace(syncSettings.UserId))
    {
        Console.WriteLine("Could not determine userId from input.");
        return 1;
    }

    var executor = app.Services.GetRequiredService<ISyncExecutionService>();
    try
    {
        var result = await executor.ExecuteAsync(syncSettings);

        Console.WriteLine($"\n>>> Done. {result.ScrapedTracks} total scraped track(s) across {result.Playlists} playlist(s).");
        Console.WriteLine($">>> Pending before download: {result.PendingBeforeDownload}");
        if (syncSettings.DownloadTracks)
            Console.WriteLine($">>> Downloads complete. Success={result.Downloaded}, Failed={result.FailedDownloads}, Output={result.OutputDirectory}");
        else
            Console.WriteLine($">>> DOWNLOAD_TRACKS=0, skipped {result.SkippedDownloads} pending track(s).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error while scraping: {ex}");
        return 2;
    }

    return 0;
}

return await MainAsync(args);
