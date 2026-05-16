namespace Pipedl.Domain.Entities;

public class Playlist
{
    public string SpotifyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public string Checksum { get; set; } = string.Empty;
    public int IsActive { get; set; } = 1;
}

public static class PlaylistHelpers
{
    // Compute a deterministic checksum from a set of track spotify ids
    public static string ComputeChecksum(IEnumerable<string> trackIds)
    {
        if (trackIds == null) return string.Empty;
        var ordered = trackIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).OrderBy(id => id).ToList();
        if (!ordered.Any()) return string.Empty;
        using var sha = System.Security.Cryptography.SHA1.Create();
        var joined = string.Join(',', ordered);
        var bytes = System.Text.Encoding.UTF8.GetBytes(joined);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    // Diff: returns (toInsert, toDelete) based on SpotifyId sets
    public static (IEnumerable<string> toInsert, IEnumerable<string> toDelete) DiffTracks(IEnumerable<string> existingIds, IEnumerable<string> scrapedIds)
    {
        var ex = new HashSet<string>((existingIds ?? Enumerable.Empty<string>()));
        var ne = new HashSet<string>((scrapedIds ?? Enumerable.Empty<string>()));

        var toInsert = ne.Except(ex).ToList();
        var toDelete = ex.Except(ne).ToList();
        return (toInsert, toDelete);
    }
}
