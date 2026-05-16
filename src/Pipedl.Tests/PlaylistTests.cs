using Pipedl.Domain.Entities;

namespace Pipedl.Tests;

public class PlaylistTests
{
    [Fact]
    public void ComputeChecksum_SameIdsDifferentOrder_ProducesSameChecksum()
    {
        var a = new[] { "id1", "id2", "id3" };
        var b = new[] { "id3", "id1", "id2" };

        var ca = PlaylistHelpers.ComputeChecksum(a);
        var cb = PlaylistHelpers.ComputeChecksum(b);

        Assert.Equal(ca, cb);
    }

    [Fact]
    public void DiffTracks_InsertsAndDeletesCalculatedCorrectly()
    {
        var existing = new[] { "a", "b", "c" };
        var scraped = new[] { "b", "c", "d", "e" };

        var (toInsert, toDelete) = PlaylistHelpers.DiffTracks(existing, scraped);

        Assert.Contains("d", toInsert);
        Assert.Contains("e", toInsert);
        Assert.Contains("a", toDelete);
    }

    [Fact]
    public void ComputeChecksum_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, PlaylistHelpers.ComputeChecksum(null));
        Assert.Equal(string.Empty, PlaylistHelpers.ComputeChecksum(Array.Empty<string>()));
    }
}
