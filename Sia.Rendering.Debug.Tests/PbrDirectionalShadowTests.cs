using System.Runtime.InteropServices;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrDirectionalShadowTests
{
    [Fact]
    public void DefaultsAreWorldDistancesAndKeepTheExistingUniformLayout()
    {
        var config = new ShadowAtlasConfig();
        Assert.Equal(.001f, config.DirectionalShadowConstantBias);
        Assert.Equal(.002f, config.DirectionalShadowSlopeBias);
        PbrDirectionalShadow.ValidateBias(config.DirectionalShadowConstantBias, config.DirectionalShadowSlopeBias);
        Assert.Equal(512, Marshal.SizeOf<PbrFrame>());
        Assert.Equal(368, Marshal.OffsetOf<PbrFrame>(nameof(PbrFrame.Splits)).ToInt32());
        Assert.Equal(384, Marshal.OffsetOf<PbrFrame>(nameof(PbrFrame.Direction0)).ToInt32());
        Assert.Equal(400, Marshal.OffsetOf<PbrFrame>(nameof(PbrFrame.Radiance0)).ToInt32());
    }

    [Theory]
    [InlineData(-.001f, .002f)]
    [InlineData(.001f, -.002f)]
    [InlineData(float.NaN, .002f)]
    [InlineData(.001f, float.NaN)]
    [InlineData(float.PositiveInfinity, .002f)]
    [InlineData(.001f, float.PositiveInfinity)]
    [InlineData(float.MaxValue, float.MaxValue)]
    public void InvalidPhysicalOffsetsAreRejected(float constant, float slope)
        => Assert.Throws<ArgumentException>(() => PbrDirectionalShadow.ValidateBias(constant, slope));

    [Fact]
    public void DisabledAndConstantOnlyOffsetsAreAllowed()
    {
        PbrDirectionalShadow.ValidateBias(0, 0);
        PbrDirectionalShadow.ValidateBias(.001f, 0);
        PbrDirectionalShadow.ValidateBias(0, .002f);
    }
}
