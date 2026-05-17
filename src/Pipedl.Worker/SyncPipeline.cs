using System.Data;
using System.Diagnostics;
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

    public async Task<SyncRunResult> RunAsync(string userId, string outputDir, bool downloadTracks, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);

        using var connection = _dbFactory.CreateConnection();
        connection.Open();
        EnsureSchema(connection);

        var playlists = (await _scraper.GetPlaylistsForUserAsync(userId)).ToList();
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
            foreach (var trackId in pendingTrackIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackUrl = $"https://open.spotify.com/track/{trackId}";
                var ok = await DownloadTrackWithSpotdlAsync(trackUrl, outputDir);
                if (ok)
                {
                    MarkTrackDownloaded(connection, trackId);
                    downloaded++;
                }
                else
                {
                    failed++;
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

    private static void MarkTrackDownloaded(IDbConnection connection, string trackId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE tracks
SET downloaded = 1,
    downloaded_at = @downloadedAt,
    last_seen_at = @seenAt
WHERE spotify_id = @id;
";

        AddParam(cmd, "@downloadedAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@seenAt", DateTime.UtcNow.ToString("O"));
        AddParam(cmd, "@id", trackId);
        cmd.ExecuteNonQuery();
    }

    private static IDbDataParameter AddParam(IDbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
        return p;
    }

    private static async Task<bool> DownloadTrackWithSpotdlAsync(string trackUrl, string outputDir)
    {
        try
        {
            var psi = new ProcessStartInfo("spotdl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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
                return true;

            Console.WriteLine($"[spotdl] Failed for {trackUrl} (exit {process.ExitCode})");
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine($"[spotdl] {stderr.Trim()}");
            else if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine($"[spotdl] {stdout.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spotdl] Exception for {trackUrl}: {ex.Message}");
            return false;
        }
    }
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
