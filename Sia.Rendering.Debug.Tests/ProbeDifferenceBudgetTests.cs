using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class ProbeDifferenceBudgetTests
{
    [Theory]
    [InlineData(2u, 1872ul, 2432ul)]
    [InlineData(4u, 13072ul, 16768ul)]
    public void DifferenceChargesOnlyItsTextureAndReferenceUniform(uint side, ulong live, ulong paired)
    {
        Assert.Equal(live, DiffuseProbeGpu.FieldBytes(new(side), true));
        Assert.Equal(paired, DiffuseProbeGpu.FieldBytes(new(side), true, true));
        Assert.Equal(live - 176, DiffuseProbeGpu.FieldBytes(new(side), false));
    }

    [Fact]
    public void BakedFieldCannotReserveAnUnusableDifference()
        => Assert.Throws<ArgumentException>(() => DiffuseProbeGpu.FieldBytes(new(2), false, true));
}
