using System.Text.RegularExpressions;

namespace Pipedl.Worker;

public sealed record SyncExecutionSettings(
    string UserId,
    string OutputDir,
    bool DownloadTracks,
    int? TargetPlaylistCount);

public sealed class RuntimeSettings
{
    private const string DefaultUserId = "mnupea";
    private const string DefaultOutputDir = "music";

    public bool IsRunOnceEnabled()
    {
        var runOnce = Environment.GetEnvironmentVariable("RUN_ONCE");
        return !string.IsNullOrEmpty(runOnce) && runOnce != "0";
    }

    public string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "pipedl.db";
        return $"Data Source={dbPath};";
    }

    public SyncExecutionSettings ResolveSyncSettings(string[]? args = null)
    {
        var input = args is { Length: > 0 }
            ? args[0]
            : (Environment.GetEnvironmentVariable("TARGET_USER_ID") ?? DefaultUserId);

        var userId = input.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? Regex.Match(input, @"/user/([^/]+)").Groups[1].Value
            : input;

        var outputDir = Environment.GetEnvironmentVariable("MUSIC_OUTPUT_PATH") ?? DefaultOutputDir;
        var downloadTracks = (Environment.GetEnvironmentVariable("DOWNLOAD_TRACKS") ?? "1") != "0";

        int? targetPlaylistCount = int.TryParse(Environment.GetEnvironmentVariable("TARGET_PLAYLIST_COUNT"), out var parsedCount) && parsedCount > 0
            ? parsedCount
            : null;

        return new SyncExecutionSettings(userId, outputDir, downloadTracks, targetPlaylistCount);
    }
}
