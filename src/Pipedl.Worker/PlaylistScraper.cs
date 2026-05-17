using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace Pipedl.Worker;

public record PlaylistInfo(string Url, string Name);
public record TrackInfo(string Url, string PlaylistName);

public class PlaylistScraper
{
    private const string BaseUrl = "https://open.spotify.com";
    private static readonly bool IsPiLike =
        RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ||
        string.Equals(Environment.GetEnvironmentVariable("PIPEDL_PI_MODE"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly string BrowserPreference =
        (Environment.GetEnvironmentVariable("PIPEDL_BROWSER") ?? (IsPiLike ? "firefox" : "chromium")).Trim().ToLowerInvariant();
    private static readonly int NavTimeoutMs = GetEnvInt("PIPEDL_NAV_TIMEOUT_MS", IsPiLike ? 180_000 : 12_000);
    private static readonly int SelectorTimeoutMs = GetEnvInt("PIPEDL_SELECTOR_TIMEOUT_MS", IsPiLike ? 180_000 : 12_000);
    private static readonly int TotalWatchdogSec = GetEnvInt("PIPEDL_TOTAL_WATCHDOG_SEC", IsPiLike ? 240 : 45);
    private static readonly int LaunchWatchdogSec = GetEnvInt("PIPEDL_LAUNCH_WATCHDOG_SEC", IsPiLike ? 90 : 30);

    // ─── Step 1: User → Playlist URLs ──────────────────────────────────────────

    public async Task<IEnumerable<PlaylistInfo>> GetPlaylistsForUserAsync(string userId)
    {
        var profileUrl = $"{BaseUrl}/user/{userId}/playlists";
        Console.WriteLine($"[Step 1] Fetching playlists for user '{userId}' from {profileUrl}");

        // Try Playwright first (handles JS rendering + infinite scroll)
        try
        {
            var results = await RunWithWatchdogAsync(async () =>
            {
                Console.WriteLine("[Step 1] Starting Playwright...");
                using var playwright = await Playwright.CreateAsync();
                Console.WriteLine($"[Step 1] Launching {BrowserPreference}...");
                await using var browser = await RunWithWatchdogAsync(
                    () => LaunchBrowserAsync(playwright, "Step 1"),
                    TimeSpan.FromSeconds(LaunchWatchdogSec),
                    "[Step 1] Browser launch watchdog timeout"
                );
                Console.WriteLine($"[Step 1] {BrowserPreference} launched.");
                Console.WriteLine($"[Step 1] Time budget: nav={NavTimeoutMs}ms selector={SelectorTimeoutMs}ms total={TotalWatchdogSec}s");
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                    Locale = "en-US",
                    IgnoreHTTPSErrors = true,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
                });
                await TryApplySpotifyCookiesAsync(context);
                var page = await context.NewPageAsync();
                Console.WriteLine("[Step 1] Navigating to profile page...");
                await RunWithWatchdogAsync(
                    () => page.GotoAsync(profileUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavTimeoutMs }),
                    TimeSpan.FromMilliseconds(NavTimeoutMs + 15_000),
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
                    await page.WaitForSelectorAsync("a[href*='/playlist/']", new PageWaitForSelectorOptions { Timeout = SelectorTimeoutMs });
                }
                catch
                {
                    // we'll continue and log diagnostics below
                }

                var elements = await page.QuerySelectorAllAsync("a[href*='/playlist/']");
                var innerResults = new List<PlaylistInfo>();
                foreach (var el in elements)
                {
                    var href = await el.GetAttributeAsync("href");
                    var name = (await el.InnerTextAsync()).Trim();
                    if (!string.IsNullOrEmpty(href) && href.Contains("/playlist/"))
                    {
                        var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                        innerResults.Add(new PlaylistInfo(fullUrl, string.IsNullOrWhiteSpace(name) ? fullUrl : name));
                    }
                }

                // Fallback extraction from page HTML for cases where anchors are virtualized.
                if (innerResults.Count == 0)
                {
                    var html = await page.ContentAsync();
                    var idMatches = Regex.Matches(html, "spotify:playlist:(?<id>[A-Za-z0-9]+)", RegexOptions.Compiled);
                    foreach (Match m in idMatches)
                    {
                        var id = m.Groups["id"].Value;
                        if (!string.IsNullOrWhiteSpace(id))
                            innerResults.Add(new PlaylistInfo($"{BaseUrl}/playlist/{id}", id));
                    }
                }

                if (innerResults.Count > 0)
                {
                    Console.WriteLine($"[Step 1] Playwright found {innerResults.Count} playlist(s).");
                    return innerResults.DistinctBy(p => p.Url).ToList();
                }

                var title = await page.TitleAsync();
                Console.WriteLine($"[Step 1] Playwright found 0 playlists. Final URL: {page.Url}");
                Console.WriteLine($"[Step 1] Page title: {title}");
                if (title.Contains("Login", StringComparison.OrdinalIgnoreCase) ||
                    page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[Step 1] Spotify likely returned a login wall for this environment.");
                }

                return innerResults.DistinctBy(p => p.Url).ToList();
            }, TimeSpan.FromSeconds(TotalWatchdogSec), "[Step 1] Playwright total watchdog timeout");

            if (results.Count > 0)
                return results;
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
            var results = await RunWithWatchdogAsync(async () =>
            {
                Console.WriteLine($"[Step 2] Starting Playwright for '{playlist.Name}'...");
                using var playwright = await Playwright.CreateAsync();
                Console.WriteLine($"[Step 2] Launching {BrowserPreference} for '{playlist.Name}'...");
                await using var browser = await RunWithWatchdogAsync(
                    () => LaunchBrowserAsync(playwright, "Step 2"),
                    TimeSpan.FromSeconds(LaunchWatchdogSec),
                    $"[Step 2] Browser launch watchdog timeout for '{playlist.Name}'"
                );
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                    Locale = "en-US",
                    IgnoreHTTPSErrors = true,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
                });
                await TryApplySpotifyCookiesAsync(context);
                var page = await context.NewPageAsync();
                await RunWithWatchdogAsync(
                    () => page.GotoAsync(playlist.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavTimeoutMs }),
                    TimeSpan.FromMilliseconds(NavTimeoutMs + 15_000),
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

                var innerResults = new List<TrackInfo>();
                foreach (var el in elements)
                {
                    var href = await el.GetAttributeAsync("href");
                    if (!string.IsNullOrEmpty(href) && href.Contains("/track/"))
                    {
                        var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                        innerResults.Add(new TrackInfo(fullUrl, playlist.Name));
                    }
                }

                if (innerResults.Count > 0)
                    Console.WriteLine($"[Step 2] Playwright found {innerResults.Count} track(s) in '{playlist.Name}'.");

                return innerResults.DistinctBy(t => t.Url).ToList();
            }, TimeSpan.FromSeconds(TotalWatchdogSec), $"[Step 2] Playwright total watchdog timeout for '{playlist.Name}'");

            if (results.Count > 0)
                return results;
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

    private static async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, string stepLabel)
    {
        if (BrowserPreference == "firefox")
        {
            var options = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = IsPiLike ? 60_000 : 20_000
            };

            return await playwright.Firefox.LaunchAsync(options);
        }

        return await playwright.Chromium.LaunchAsync(CreateChromiumLaunchOptions(stepLabel));
    }

    private static BrowserTypeLaunchOptions CreateChromiumLaunchOptions(string stepLabel)
    {
        var args = new List<string>
        {
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-blink-features=AutomationControlled",
            "--disable-dev-shm-usage"
        };

        if (IsPiLike)
        {
            Console.WriteLine($"[{stepLabel}] Pi mode enabled: applying ARM stability flags.");
            args.AddRange(
            [
                "--disable-gpu",
                "--disable-software-rasterizer",
                "--no-zygote",
                "--single-process",
                "--disable-extensions",
                "--disable-background-networking",
                "--disable-features=Translate,BackForwardCache"
            ]);
        }

        var options = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Timeout = IsPiLike ? 45_000 : 15_000,
            Args = args
        };

        if (IsPiLike)
        {
            foreach (var candidate in new[] { "/usr/bin/chromium-browser", "/usr/bin/chromium", "/snap/bin/chromium" })
            {
                if (File.Exists(candidate))
                {
                    options.ExecutablePath = candidate;
                    Console.WriteLine($"[{stepLabel}] Using system Chromium: {candidate}");
                    break;
                }
            }
        }

        return options;
    }

    private static async Task TryApplySpotifyCookiesAsync(IBrowserContext context)
    {
        var cookieHeader = Environment.GetEnvironmentVariable("SPOTIFY_COOKIE");
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return;

        var cookies = new List<Cookie>();
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0 || idx >= part.Length - 1)
                continue;

            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            cookies.Add(new Cookie
            {
                Name = name,
                Value = value,
                Domain = ".spotify.com",
                Path = "/",
                Secure = true,
                HttpOnly = false
            });
        }

        if (cookies.Count > 0)
        {
            await context.AddCookiesAsync(cookies);
            Console.WriteLine($"[Scraper] Applied {cookies.Count} Spotify cookie(s) from SPOTIFY_COOKIE.");
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

    private static async Task<T> RunWithWatchdogAsync<T>(Func<Task<T>> action, TimeSpan timeout, string timeoutMessage)
    {
        var task = action();
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            throw new TimeoutException(timeoutMessage);

        return await task;
    }

    private static int GetEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : defaultValue;
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
