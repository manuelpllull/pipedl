using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Pipedl.Infrastructure;
using Pipedl.Worker;

async Task<int> MainAsync(string[] args)
{
    using IHost host = Host.CreateDefaultBuilder(args)
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<RuntimeSettings>();
            services.AddSingleton<DbConnectionFactory>(sp =>
            {
                var runtimeSettings = sp.GetRequiredService<RuntimeSettings>();
                return new DbConnectionFactory(runtimeSettings.ResolveConnectionString());
            });

            services.AddSingleton<PlaylistScraper>();
            services.AddSingleton<SyncPipeline>();

            services.AddQuartz(q =>
            {
                var jobKey = new JobKey("SyncJob");
                q.AddJob<SyncJob>(opts => opts.WithIdentity(jobKey));

                q.AddTrigger(opts => opts
                    .ForJob(jobKey)
                    .WithIdentity("SyncJob-trigger")
                    .WithCronSchedule("0 0 * * * ?"));
            });

            services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        })
        .Build();

    var runtime = host.Services.GetRequiredService<RuntimeSettings>();
    if (!runtime.IsRunOnceEnabled())
    {
        await host.RunAsync();
        return 0;
    }

    var syncSettings = runtime.ResolveSyncSettings(args);
    if (string.IsNullOrWhiteSpace(syncSettings.UserId))
    {
        Console.WriteLine("Could not determine userId from input.");
        return 1;
    }

    var pipeline = host.Services.GetRequiredService<SyncPipeline>();
    try
    {
        var result = await pipeline.RunAsync(
            syncSettings.UserId,
            syncSettings.OutputDir,
            syncSettings.DownloadTracks,
            syncSettings.TargetPlaylistCount);

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
