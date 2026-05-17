using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Pipedl.Infrastructure;
using Pipedl.Worker;

async Task<int> MainAsync(string[] args)
{
    var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "pipedl.db";
    var dbFactory = new DbConnectionFactory($"Data Source={dbPath};Version=3;Journal Mode=WAL;");

    // If RUN_ONCE environment variable is set, run a full two-step scrape: user → playlists → tracks
    var runOnce = Environment.GetEnvironmentVariable("RUN_ONCE");
    if (!string.IsNullOrEmpty(runOnce) && runOnce != "0")
    {
        // Accept userId from: CLI arg → TARGET_USER_ID env var → default
        var input = args.Length > 0
            ? args[0]
            : (Environment.GetEnvironmentVariable("TARGET_USER_ID") ?? "mnupea");
        var userId = input.StartsWith("http")
            ? System.Text.RegularExpressions.Regex.Match(input, @"/user/([^/]+)").Groups[1].Value
            : input;

        if (string.IsNullOrEmpty(userId))
        {
            Console.WriteLine("Could not determine userId from input.");
            return 1;
        }

        var scraper = new PlaylistScraper();
        var pipeline = new SyncPipeline(dbFactory, scraper);
        var outputDir = Environment.GetEnvironmentVariable("MUSIC_OUTPUT_PATH") ?? "music";
        var downloadTracks = (Environment.GetEnvironmentVariable("DOWNLOAD_TRACKS") ?? "1") != "0";

        try
        {
            var result = await pipeline.RunAsync(userId, outputDir, downloadTracks);

            Console.WriteLine($"\n>>> Done. {result.ScrapedTracks} total scraped track(s) across {result.Playlists} playlist(s).");
            Console.WriteLine($">>> Pending before download: {result.PendingBeforeDownload}");
            if (downloadTracks)
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

    IHost host = Host.CreateDefaultBuilder(args)
        .ConfigureServices((hostContext, services) =>
        {
            services.AddSingleton(dbFactory);
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

    await host.RunAsync();
    return 0;
}

return await MainAsync(args);
