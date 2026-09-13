using Xunit;

namespace Ai4Sts2.Core.Tests;

public sealed class ZobristTests
{
    [Fact]
    public void ReplaceRoundTripsToOriginalHash()
    {
        var z = new Zobrist(8, 16, 42);
        var h = z.Key(0, 3) ^ z.Key(1, 5);
        var moved = z.Replace(h, 1, 5, 9);
        Assert.NotEqual(h, moved);
        Assert.Equal(h, z.Replace(moved, 1, 9, 5));
    }

    [Fact]
    public void SameSeedProducesSameKeys()
    {
        var a = new Zobrist(4, 4, 7);
        var b = new Zobrist(4, 4, 7);
        for (var s = 0; s < 4; s++)
        {
            for (var v = 0; v < 4; v++)
            {
                Assert.Equal(a.Key(s, v), b.Key(s, v));
            }
        }
    }

    [Fact]
    public void KeysAreDistinct()
    {
        var z = new Zobrist(32, 32, 1);
        var seen = new HashSet<ulong>();
        for (var s = 0; s < 32; s++)
        {
            for (var v = 0; v < 32; v++)
            {
                Assert.True(seen.Add(z.Key(s, v)));
            }
        }
    }
}
