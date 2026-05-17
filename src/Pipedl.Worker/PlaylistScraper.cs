using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Pipedl.Worker;

public record PlaylistInfo(string Url, string Name);
public record TrackInfo(string Url, string PlaylistName);

public class PlaylistScraper
{
    private const string BaseUrl = "https://open.spotify.com";

    // ─── Step 1: User → Playlist URLs ──────────────────────────────────────────

    public async Task<IEnumerable<PlaylistInfo>> GetPlaylistsForUserAsync(string userId)
    {
        var profileUrl = $"{BaseUrl}/user/{userId}/playlists";
        Console.WriteLine($"[Step 1] Fetching playlists for user '{userId}' from {profileUrl}");

        // Try Playwright first (handles JS rendering + infinite scroll)
        try
        {
            Console.WriteLine("[Step 1] Starting Playwright...");
            using var playwright = await Playwright.CreateAsync();
            Console.WriteLine("[Step 1] Launching Chromium...");
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 15_000,
                Args = ["--no-sandbox", "--disable-blink-features=AutomationControlled"]
            });
            Console.WriteLine("[Step 1] Chromium launched.");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                Locale = "en-US"
            });
            var page = await context.NewPageAsync();
            Console.WriteLine("[Step 1] Navigating to profile page...");
            await RunWithWatchdogAsync(
                () => page.GotoAsync(profileUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 12_000 }),
                TimeSpan.FromSeconds(15),
                "[Step 1] Navigation watchdog timeout"
            );

            // Accept cookie banner if present (common on fresh headless sessions)
            var acceptCookies = page.GetByRole(AriaRole.Button, new() { Name = "Accept cookies" });
            if (await acceptCookies.CountAsync() > 0)
            {
                await acceptCookies.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(800);
            }

            // Let client-side app hydrate before scanning anchors
            await page.WaitForTimeoutAsync(2_500);

            // Infinite scroll: keep scrolling until no new items appear
            await ScrollToBottomAsync(page);

            // Wait for at least one candidate link if available
            try
            {
                await page.WaitForSelectorAsync("a[href*='/playlist/']", new PageWaitForSelectorOptions { Timeout = 12_000 });
            }
            catch
            {
                // we'll continue and log diagnostics below
            }

            var elements = await page.QuerySelectorAllAsync("a[href*='/playlist/']");
            var results = new List<PlaylistInfo>();
            foreach (var el in elements)
            {
                var href = await el.GetAttributeAsync("href");
                var name = (await el.InnerTextAsync()).Trim();
                if (!string.IsNullOrEmpty(href) && href.Contains("/playlist/"))
                {
                    var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                    results.Add(new PlaylistInfo(fullUrl, string.IsNullOrWhiteSpace(name) ? fullUrl : name));
                }
            }

            if (results.Count > 0)
            {
                Console.WriteLine($"[Step 1] Playwright found {results.Count} playlist(s).");
                return results.DistinctBy(p => p.Url).ToList();
            }

            var title = await page.TitleAsync();
            Console.WriteLine($"[Step 1] Playwright found 0 playlists. Final URL: {page.Url}");
            Console.WriteLine($"[Step 1] Page title: {title}");
            if (title.Contains("Login", StringComparison.OrdinalIgnoreCase) ||
                page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Step 1] Spotify likely returned a login wall for this environment.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Step 1] Playwright failed: {ex.Message}");
        }

        // HTTP fallback
        Console.WriteLine("[Step 1] Falling back to HTTP+regex...");
        return await FallbackGetPlaylistsAsync(profileUrl);
    }

    // ─── Step 2: Playlist URL → Track URLs ─────────────────────────────────────

    public async Task<IEnumerable<TrackInfo>> GetTracksForPlaylistAsync(PlaylistInfo playlist)
    {
        Console.WriteLine($"[Step 2] Scraping tracks from playlist '{playlist.Name}' ({playlist.Url})");

        try
        {
            Console.WriteLine($"[Step 2] Starting Playwright for '{playlist.Name}'...");
            using var playwright = await Playwright.CreateAsync();
            Console.WriteLine($"[Step 2] Launching Chromium for '{playlist.Name}'...");
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 15_000,
                Args = ["--no-sandbox", "--disable-blink-features=AutomationControlled"]
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                Locale = "en-US"
            });
            var page = await context.NewPageAsync();
            await RunWithWatchdogAsync(
                () => page.GotoAsync(playlist.Url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 12_000 }),
                TimeSpan.FromSeconds(15),
                $"[Step 2] Navigation watchdog timeout for '{playlist.Name}'"
            );

            var acceptCookies = page.GetByRole(AriaRole.Button, new() { Name = "Accept cookies" });
            if (await acceptCookies.CountAsync() > 0)
            {
                await acceptCookies.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(800);
            }

            await page.WaitForTimeoutAsync(2_000);

            await ScrollToBottomAsync(page);

            // Prefer data-testid=tracklist-row, fall back to any href containing /track/
            var elements = await page.QuerySelectorAllAsync("[data-testid='tracklist-row'] a[href*='/track/']");
            if (elements.Count == 0)
                elements = await page.QuerySelectorAllAsync("a[href*='/track/']");

            var results = new List<TrackInfo>();
            foreach (var el in elements)
            {
                var href = await el.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href) && href.Contains("/track/"))
                {
                    var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                    results.Add(new TrackInfo(fullUrl, playlist.Name));
                }
            }

            if (results.Count > 0)
            {
                Console.WriteLine($"[Step 2] Playwright found {results.Count} track(s) in '{playlist.Name}'.");
                return results.DistinctBy(t => t.Url).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Step 2] Playwright failed for playlist '{playlist.Name}': {ex.Message}");
        }

        // HTTP fallback
        Console.WriteLine($"[Step 2] Falling back to HTTP+regex for '{playlist.Name}'...");
        return await FallbackGetTracksAsync(playlist);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task ScrollToBottomAsync(IPage page)
    {
        int previous = 0;
        for (int i = 0; i < 20; i++)
        {
            await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(800);
            var current = await page.EvaluateAsync<int>("document.body.scrollHeight");
            if (current == previous) break;
            previous = current;
        }
    }

    private static async Task RunWithWatchdogAsync(Func<Task> action, TimeSpan timeout, string timeoutMessage)
    {
        var task = action();
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            throw new TimeoutException(timeoutMessage);

        await task;
    }

    private static async Task<IEnumerable<PlaylistInfo>> FallbackGetPlaylistsAsync(string profileUrl)
    {
        var results = new List<PlaylistInfo>();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            var html = await http.GetStringAsync(profileUrl);
            var matches = Regex.Matches(html, "/playlist/(?<id>[A-Za-z0-9]+)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                var url = BaseUrl + m.Value;
                results.Add(new PlaylistInfo(url, m.Groups["id"].Value));
            }
            Console.WriteLine($"[Step 1] Fallback found {results.Count} playlist(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Step 1] Fallback failed: {ex.Message}");
        }
        return results.DistinctBy(p => p.Url).ToList();
    }

    private static async Task<IEnumerable<TrackInfo>> FallbackGetTracksAsync(PlaylistInfo playlist)
    {
        var results = new List<TrackInfo>();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            var html = await http.GetStringAsync(playlist.Url);
            var matches = Regex.Matches(html, "/track/(?<id>[A-Za-z0-9]+)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                var url = BaseUrl + m.Value;
                results.Add(new TrackInfo(url, playlist.Name));
            }
            Console.WriteLine($"[Step 2] Fallback found {results.Count} track(s) in '{playlist.Name}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Step 2] Fallback failed for '{playlist.Name}': {ex.Message}");
        }
        return results.DistinctBy(t => t.Url).ToList();
    }
}
