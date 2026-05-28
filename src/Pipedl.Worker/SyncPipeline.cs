using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pipedl.Infrastructure;

namespace Pipedl.Worker;

public class SyncPipeline
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly PlaylistScraper _scraper;

    public SyncPipeline(DbConnectionFactory dbFactory, PlaylistScraper scraper)
    {
        _dbFactory = dbFactory;
        _scraper = scraper;
    }

    public async Task<SyncRunResult> RunAsync(
        string userId,
        string outputDir,
        bool downloadTracks,
        int? targetPlaylistCount = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);

        using var connection = _dbFactory.CreateConnection();
        connection.Open();
        EnsureSchema(connection);

        var playlists = (await _scraper.GetPlaylistsForUserAsync(userId)).ToList();

        if (targetPlaylistCount.HasValue && targetPlaylistCount.Value > 0)
        {
            playlists = playlists.Take(targetPlaylistCount.Value).ToList();
            Console.WriteLine($">>> TARGET_PLAYLIST_COUNT set. Taking first {playlists.Count} playlist(s).");
        }

        Console.WriteLine($"\n>>> Found {playlists.Count} playlist(s) for user '{userId}'.");

        var playlistUrls = new HashSet<string>(playlists.Select(p => p.Url), StringComparer.OrdinalIgnoreCase);
        MarkPlaylistsInactiveNotIn(connection, playlistUrls);

        var totalScrapedTracks = 0;

        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playlistId = ExtractPlaylistId(playlist.Url);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                Console.WriteLine($"[db] Skipping playlist with invalid URL: {playlist.Url}");
                continue;
            }

            var tracks = (await _scraper.GetTracksForPlaylistAsync(playlist)).ToList();
            totalScrapedTracks += tracks.Count;

            var trackIds = tracks
                .Select(t => ExtractTrackId(t.Url))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            UpsertPlaylist(connection, playlistId, playlist.Name, userId, trackIds);
            ReplacePlaylistTracks(connection, playlistId, trackIds);
        }

        var pendingTrackIds = GetPendingTrackIds(connection);
        Console.WriteLine($">>> DB updated. Pending download count: {pendingTrackIds.Count}");

        var downloaded = 0;
        var failed = 0;
        var skipped = 0;

        if (!downloadTracks)
        {
            skipped = pendingTrackIds.Count;
        }
        else
        {
            var spotdlExecutable = ResolveSpotdlExecutablePath();

            foreach (var trackId in pendingTrackIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackUrl = $"https://open.spotify.com/track/{trackId}";
                var downloadResult = await DownloadTrackWithSpotdlAsync(spotdlExecutable, trackUrl, outputDir);
                if (downloadResult.Success)
                {
                    MarkTrackDownloaded(connection, trackId, downloadResult.Metadata);
                    downloaded++;
                }
                else
                {
                    failed++;
                }
            }

            var staleMetadataTrackIds = GetDownloadedTrackIdsMissingMetadata(connection);
            if (staleMetadataTrackIds.Count > 0)
            {
                Console.WriteLine($">>> Backfilling metadata for {staleMetadataTrackIds.Count} downloaded track(s) missing title/artist/album/duration...");

                foreach (var trackId in staleMetadataTrackIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var trackUrl = $"https://open.spotify.com/track/{trackId}";
                    var metadata = await GetTrackMetadataFromSpotdlAsync(spotdlExecutable, trackUrl);
                    if (metadata is not null)
                        UpdateTrackMetadata(connection, trackId, metadata);
                }
            }
        }

        return new SyncRunResult
        {
            Playlists = playlists.Count,
            ScrapedTracks = totalScrapedTracks,
            PendingBeforeDownload = pendingTrackIds.Count,
            Downloaded = downloaded,
            FailedDownloads = failed,
            SkippedDownloads = skipped,
            OutputDirectory = outputDir
        };
    }

    private static string? ExtractPlaylistId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url ?? string.Empty, @"/playlist/(?<id>[A-Za-z0-9]+)");
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static string? ExtractTrackId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url ?? string.Empty, @"/track/(?<id>[A-Za-z0-9]+)");
        return m.Success ? m.Groups["id"].Value : null;
    }

    private static void EnsureSchema(IDbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS playlists (
  spotify_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  owner TEXT NOT NULL,
  last_synced_at TEXT,
  checksum TEXT,
  is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS tracks (
  spotify_id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  artist TEXT,
  album TEXT,
  duration INTEGER NOT NULL DEFAULT 0,
  downloaded INTEGER NOT NULL DEFAULT 0,
  downloaded_at TEXT,
  last_seen_at TEXT
);

CREATE TABLE IF NOT EXISTS playlist_tracks (
  playlist_id TEXT NOT NULL,
  track_id TEXT NOT NULL,
  first_seen_at TEXT NOT NULL,
  last_seen_at TEXT NOT NULL,
  PRIMARY KEY (playlist_id, track_id),
  FOREIGN KEY (playlist_id) REFERENCES playlists(spotify_id),
  FOREIGN KEY (track_id) REFERENCES tracks(spotify_id)
);

CREATE INDEX IF NOT EXISTS idx_tracks_downloaded ON tracks(downloaded);
CREATE INDEX IF NOT EXISTS idx_playlist_tracks_track_id ON playlist_tracks(track_id);
";
        cmd.ExecuteNonQuery();
    }

    private static void MarkPlaylistsInactiveNotIn(IDbConnection connection, HashSet<string> activeUrls)
    {
        // We only know playlist Spotify IDs in DB, so convert URL set first.
        var activeIds = activeUrls
            .Select(ExtractPlaylistId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        using var tx = connection.BeginTransaction();

        using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = tx;
        selectCmd.CommandText = "SELECT spotify_id FROM playlists";
        var existingIds = new List<string>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                existingIds.Add(reader.GetString(0));
            }
        }

        using var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = "UPDATE playlists SET is_active=@isActive WHERE spotify_id=@id";

        var pIsActive = updateCmd.CreateParameter();
        pIsActive.ParameterName = "@isActive";
        updateCmd.Parameters.Add(pIsActive);

        var pId = updateCmd.CreateParameter();
        pId.ParameterName = "@id";
        updateCmd.Parameters.Add(pId);

        foreach (var id in existingIds)
        {
            pIsActive.Value = activeIds.Contains(id) ? 1 : 0;
            pId.Value = id;
            updateCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void UpsertPlaylist(IDbConnection connection, string playlistId, string playlistName, string owner, List<string> trackIds)
    {
        var checksum = Pipedl.Domain.Entities.PlaylistHelpers.ComputeChecksum(trackIds);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO playlists (spotify_id, name, owner, last_synced_at, checksum, is_active)
VALUES (@id, @name, @owner, @syncedAt, @checksum, 1)
ON CONFLICT(spotify_id) DO UPDATE SET
  name=excluded.name,
  owner=excluded.owner,
  last_synced_at=excluded.last_synced_at,
  checksum=excluded.checksum,
  is_active=1;
";

        AddParam(cmd, "@id", playlistId);
        AddParam(cmd, "@name", string.IsNullOrWhiteSpace(playlistName) ? playlistId : playlistName);
        AddParam(cmd, "@owner", owner);
        AddParam(cmd, "@syncedAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@checksum", checksum);
        cmd.ExecuteNonQuery();
    }

    private static void ReplacePlaylistTracks(IDbConnection connection, string playlistId, List<string> trackIds)
    {
        using var tx = connection.BeginTransaction();

        // Ensure all tracks exist.
        using (var upsertTrack = connection.CreateCommand())
        {
            upsertTrack.Transaction = tx;
            upsertTrack.CommandText = @"
INSERT INTO tracks (spotify_id, title, artist, album, duration, downloaded, last_seen_at)
VALUES (@id, @title, '', '', 0, 0, @seenAt)
ON CONFLICT(spotify_id) DO UPDATE SET
  last_seen_at=excluded.last_seen_at;
";

            var pId = AddParam(upsertTrack, "@id", string.Empty);
            var pTitle = AddParam(upsertTrack, "@title", string.Empty);
            var pSeenAt = AddParam(upsertTrack, "@seenAt", DateTime.UtcNow.ToString("O"));

            foreach (var trackId in trackIds)
            {
                pId.Value = trackId;
                pTitle.Value = trackId;
                pSeenAt.Value = DateTime.UtcNow.ToString("O");
                upsertTrack.ExecuteNonQuery();
            }
        }

        // Replace membership to reflect latest scrape exactly.
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id=@playlistId";
            AddParam(deleteCmd, "@playlistId", playlistId);
            deleteCmd.ExecuteNonQuery();
        }

        using (var insertMembership = connection.CreateCommand())
        {
            insertMembership.Transaction = tx;
            insertMembership.CommandText = @"
INSERT INTO playlist_tracks (playlist_id, track_id, first_seen_at, last_seen_at)
VALUES (@playlistId, @trackId, @nowTs, @nowTs);
";

            var pPlaylistId = AddParam(insertMembership, "@playlistId", playlistId);
            var pTrackId = AddParam(insertMembership, "@trackId", string.Empty);
            var pNowTs = AddParam(insertMembership, "@nowTs", DateTime.UtcNow.ToString("O"));

            foreach (var trackId in trackIds)
            {
                pPlaylistId.Value = playlistId;
                pTrackId.Value = trackId;
                pNowTs.Value = DateTime.UtcNow.ToString("O");
                insertMembership.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static List<string> GetPendingTrackIds(IDbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT t.spotify_id
FROM tracks t
JOIN playlist_tracks pt ON pt.track_id = t.spotify_id
JOIN playlists p ON p.spotify_id = pt.playlist_id
WHERE p.is_active = 1
  AND IFNULL(t.downloaded, 0) = 0
ORDER BY t.spotify_id;
";

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static void MarkTrackDownloaded(IDbConnection connection, string trackId, SpotdlTrackMetadata? metadata)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE tracks
SET downloaded = 1,
    title = CASE WHEN @title IS NOT NULL AND @title <> '' THEN @title ELSE title END,
    artist = CASE WHEN @artist IS NOT NULL AND @artist <> '' THEN @artist ELSE artist END,
    album = CASE WHEN @album IS NOT NULL AND @album <> '' THEN @album ELSE album END,
    duration = CASE WHEN @duration > 0 THEN @duration ELSE duration END,
    downloaded_at = @downloadedAt,
    last_seen_at = @seenAt
WHERE spotify_id = @id;
";

        AddParam(cmd, "@title", metadata?.Title);
        AddParam(cmd, "@artist", metadata?.Artist);
        AddParam(cmd, "@album", metadata?.Album);
        AddParam(cmd, "@duration", metadata?.DurationSeconds ?? 0);
        AddParam(cmd, "@downloadedAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@seenAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@id", trackId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateTrackMetadata(IDbConnection connection, string trackId, SpotdlTrackMetadata metadata)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE tracks
SET title = CASE WHEN @title IS NOT NULL AND @title <> '' THEN @title ELSE title END,
    artist = CASE WHEN @artist IS NOT NULL AND @artist <> '' THEN @artist ELSE artist END,
    album = CASE WHEN @album IS NOT NULL AND @album <> '' THEN @album ELSE album END,
    duration = CASE WHEN @duration > 0 THEN @duration ELSE duration END,
    last_seen_at = @seenAt
WHERE spotify_id = @id;
";

        AddParam(cmd, "@title", metadata.Title);
        AddParam(cmd, "@artist", metadata.Artist);
        AddParam(cmd, "@album", metadata.Album);
        AddParam(cmd, "@duration", metadata.DurationSeconds);
        AddParam(cmd, "@seenAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@id", trackId);
        cmd.ExecuteNonQuery();
    }

    private static List<string> GetDownloadedTrackIdsMissingMetadata(IDbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT t.spotify_id
FROM tracks t
JOIN playlist_tracks pt ON pt.track_id = t.spotify_id
JOIN playlists p ON p.spotify_id = pt.playlist_id
WHERE p.is_active = 1
  AND IFNULL(t.downloaded, 0) = 1
  AND (
    t.title = t.spotify_id
    OR IFNULL(t.artist, '') = ''
    OR IFNULL(t.album, '') = ''
    OR IFNULL(t.duration, 0) = 0
  )
ORDER BY t.spotify_id;
";

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static IDbDataParameter AddParam(IDbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
        return p;
    }

    private static async Task<SpotdlDownloadResult> DownloadTrackWithSpotdlAsync(string spotdlExecutable, string trackUrl, string outputDir)
    {
        try
        {
            var psi = new ProcessStartInfo(spotdlExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("download");
            psi.ArgumentList.Add(trackUrl);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outputDir);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            var stdout = await stdOutTask;
            var stderr = await stdErrTask;

            if (process.ExitCode == 0)
            {
                var metadata = await GetTrackMetadataFromSpotdlAsync(spotdlExecutable, trackUrl);
                return new SpotdlDownloadResult(true, metadata);
            }

            Console.WriteLine($"[spotdl] Failed for {trackUrl} (exit {process.ExitCode})");
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine($"[spotdl] {stderr.Trim()}");
            else if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine($"[spotdl] {stdout.Trim()}");
            return SpotdlDownloadResult.Failed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spotdl] Exception for {trackUrl}: {ex.Message}");
            return SpotdlDownloadResult.Failed;
        }
    }

    private static async Task<SpotdlTrackMetadata?> GetTrackMetadataFromSpotdlAsync(string spotdlExecutable, string trackUrl)
    {
        try
        {
            var psi = new ProcessStartInfo(spotdlExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("save");
            psi.ArgumentList.Add(trackUrl);
            psi.ArgumentList.Add("--save-file");
            psi.ArgumentList.Add("-");

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine($"[spotdl] Metadata lookup failed for {trackUrl}: {stderr.Trim()}");
                return null;
            }

            var json = ExtractFirstJsonArray(stdout);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var songs = JsonSerializer.Deserialize<List<SpotdlSaveSong>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            var first = songs?.FirstOrDefault();
            if (first is null)
                return null;

            var artist = !string.IsNullOrWhiteSpace(first.Artist)
                ? first.Artist
                : string.Join(", ", first.Artists?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? []);

            return new SpotdlTrackMetadata(
                first.Name ?? string.Empty,
                artist,
                first.AlbumName ?? string.Empty,
                first.Duration ?? 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spotdl] Metadata parse exception for {trackUrl}: {ex.Message}");
            return null;
        }
    }

    private static string ResolveSpotdlExecutablePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("SPOTDL_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var cursor = new DirectoryInfo(Environment.CurrentDirectory);
        while (cursor is not null)
        {
            var localVenvPath = Path.Combine(cursor.FullName, ".venv-spotdl", "bin", "spotdl");
            if (File.Exists(localVenvPath))
                return localVenvPath;

            cursor = cursor.Parent;
        }

        return "spotdl";
    }

    private static string? ExtractFirstJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;

        return text[start..(end + 1)];
    }
}

sealed record SpotdlDownloadResult(bool Success, SpotdlTrackMetadata? Metadata)
{
    public static SpotdlDownloadResult Failed { get; } = new(false, null);
}

sealed record SpotdlTrackMetadata(string Title, string Artist, string Album, int DurationSeconds);

sealed class SpotdlSaveSong
{
    public string? Name { get; init; }
    public string? Artist { get; init; }
    public List<string>? Artists { get; init; }
    [JsonPropertyName("album_name")]
    public string? AlbumName { get; init; }
    public int? Duration { get; init; }
}

public class SyncRunResult
{
    public int Playlists { get; set; }
    public int ScrapedTracks { get; set; }
    public int PendingBeforeDownload { get; set; }
    public int Downloaded { get; set; }
    public int FailedDownloads { get; set; }
    public int SkippedDownloads { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
}
