using Xunit;

namespace Ai4Sts2.Core.Tests;

public sealed class PowerWeightsTests
{
    [Theory]
    [InlineData("STRENGTH_POWER", true, 30, 20)]
    [InlineData("STRENGTH", false, 30, 20)]
    [InlineData("VULNERABLE_POWER", true, 20, 6)]
    [InlineData("PLOW_POWER", true, 0, 0)]
    [InlineData("RINGING_POWER", false, 25, 1)]
    public void KnownPowersCarrySuffixInsensitiveWeights(string id, bool enemy, int weight, int cap) =>
        Assert.Equal((weight, cap), PowerWeights.For(id, enemy));

    [Fact]
    public void UnknownEnemyPowersAreIgnoredAndUnknownPlayerPowersAreSmall()
    {
        Assert.Equal((0, 0), PowerWeights.For("MYSTERY_POWER", true));
        Assert.Equal((5, 10), PowerWeights.For("MYSTERY_POWER", false));
    }
}
