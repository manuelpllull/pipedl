using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Pipedl.Infrastructure;
using Pipedl.Worker;

async Task<int> MainAsync(string[] args)
{
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

        try
        {
            // ── Step 1: get playlists ─────────────────────────────────────────
            var playlists = (await scraper.GetPlaylistsForUserAsync(userId)).ToList();
            Console.WriteLine($"\n>>> Found {playlists.Count} playlist(s) for user '{userId}':\n");
            foreach (var p in playlists)
                Console.WriteLine($"  📂 {p.Name}  →  {p.Url}");

            if (playlists.Count == 0)
            {
                Console.WriteLine("No playlists found — Spotify may require login for this profile.");
                return 0;
            }

            // ── Step 2: get tracks per playlist ──────────────────────────────
            Console.WriteLine("\n>>> Fetching tracks from each playlist...\n");
            var total = 0;
            foreach (var playlist in playlists)
            {
                var tracks = (await scraper.GetTracksForPlaylistAsync(playlist)).ToList();
                total += tracks.Count;
                Console.WriteLine($"\n  🎵 '{playlist.Name}' — {tracks.Count} track(s):");
                foreach (var t in tracks)
                    Console.WriteLine($"      {t.Url}");
            }

            Console.WriteLine($"\n>>> Done. {total} total track(s) across {playlists.Count} playlist(s).");
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
            var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "pipedl.db";
            services.AddSingleton(new DbConnectionFactory($"Data Source={dbPath};Version=3;Journal Mode=WAL;"));
            
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
