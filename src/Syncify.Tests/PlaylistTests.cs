using Syncify.Domain.Entities;
using Xunit;

namespace Syncify.Tests;

public class PlaylistTests
{
    [Fact]
    public void ComputeChecksum_SameIdsDifferentOrder_ProducesSameChecksum()
    {
        var a = new[] { "id3", "id1", "id2" };
        var b = new[] { "id1", "id2", "id3" };

        var ca = PlaylistHelpers.ComputeChecksum(a);
        var cb = PlaylistHelpers.ComputeChecksum(b);

        Assert.False(string.IsNullOrWhiteSpace(ca));
        Assert.Equal(ca, cb);
    }

    [Fact]
    public void DiffTracks_InsertsAndDeletesCalculatedCorrectly()
    {
        var existing = new[] { "a", "b", "c" };
        var scraped = new[] { "b", "c", "d", "e" };

        var (toInsert, toDelete) = PlaylistHelpers.DiffTracks(existing, scraped);

        Assert.Equal(new[] { "d", "e" }.OrderBy(x => x), toInsert.OrderBy(x => x));
        Assert.Equal(new[] { "a" }, toDelete);
    }

    [Fact]
    public void ComputeChecksum_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, PlaylistHelpers.ComputeChecksum(null));
        Assert.Equal(PlaylistHelpers.ComputeChecksum(Enumerable.Empty<string>()), PlaylistHelpers.ComputeChecksum(new string[0]));
    }
}
