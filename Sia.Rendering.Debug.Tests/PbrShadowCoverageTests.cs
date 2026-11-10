using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrShadowCoverageTests
{
    private const int TileSize = 128;
    private const int Samples = TileSize * TileSize;

    [Theory]
    [InlineData(false, 1f, 0f, 0f, false)]
    [InlineData(true, 0f, 0f, 0f, false)]
    [InlineData(true, .25f, 0f, 0f, true)]
    [InlineData(true, 1f, 0f, 0f, true)]
    [InlineData(true, 1f, 1f, 0f, true)]
    [InlineData(true, 1f, 1f, 1f, true)]
    public void CandidateClassifiesAlphaPresenceBeforeTransportEvaluation(
        bool blend, float opacity, float transmission, float metallic, bool expected)
    {
        var material = new PbrMaterialAsset(PbrMaterial.Default with { Metallic = metallic },
            AlphaBlend: blend, Opacity: opacity, Transmission: transmission);
        // Candidate membership must not drop fully metallic alpha just because
        // stored transmission is positive. Effective transport belongs to shading.
        Assert.Equal(expected, PbrShadowCoverage.IsCandidate(material));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1753845952u)]
    [InlineData(uint.MaxValue, 1734902346u)]
    [InlineData(0x9e3779b9u, 33350994u)]
    [InlineData(0x80000000u, 3427483940u)]
    public void IntegerHashHasFixedUncheckedGoldenVectors(uint value, uint expected)
        => Assert.Equal(expected, PbrShadowCoverage.Hash(value));

    [Theory]
    [InlineData(0u, 0u, 0u, 0u, 0u, 246052113u)]
    [InlineData(1u, 2u, 3u, 4u, 5u, 2900102592u)]
    [InlineData(127u, 127u, 30u, 17u, 42u, 746445645u)]
    [InlineData(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, 2361334799u)]
    [InlineData(1024u, 720u, 7u, 268435455u, uint.MaxValue, 445688954u)]
    public void StableHashPreservesThePixelLightInstanceAndPrimitiveProtocol(
        uint x, uint y, uint light, uint instance, uint primitive, uint expected)
        => Assert.Equal(expected, PbrShadowCoverage.StableHash(x, y, light, instance, primitive));

    [Fact]
    public void ThresholdUsesTheUpper24BitsWithoutEverReachingOne()
    {
        Assert.Equal(0f, PbrShadowCoverage.Threshold(0));
        Assert.Equal(0f, PbrShadowCoverage.Threshold(255));
        Assert.Equal(1f / 16777216, PbrShadowCoverage.Threshold(256));
        Assert.Equal(.5f, PbrShadowCoverage.Threshold(0x80000000u));
        Assert.Equal(MathF.BitDecrement(1f), PbrShadowCoverage.Threshold(uint.MaxValue));
        for (uint y = 0; y < TileSize; y++)
            for (uint x = 0; x < TileSize; x++) {
                var threshold = PbrShadowCoverage.Threshold(PbrShadowCoverage.StableHash(x, y, 3, 17, 42));
                Assert.True(float.IsFinite(threshold));
                Assert.True(threshold >= 0 && threshold < 1);
            }
    }

    [Fact]
    public void ZeroAndOneCoverageHaveExactEndpointsAndThresholdTiesDiscard()
    {
        for (uint y = 0; y < TileSize; y++)
            for (uint x = 0; x < TileSize; x++) {
                var hash = PbrShadowCoverage.StableHash(x, y, 3, 17, 42);
                Assert.False(PbrShadowCoverage.Covers(0, hash));
                Assert.True(PbrShadowCoverage.Covers(1, hash));
            }
        Assert.False(PbrShadowCoverage.Covers(0, 0));
        Assert.True(PbrShadowCoverage.Covers(1, uint.MaxValue));
        Assert.False(PbrShadowCoverage.Covers(.5f, 0x80000000u));
        Assert.True(PbrShadowCoverage.Covers(MathF.BitIncrement(.5f), 0x80000000u));
    }

    [Fact]
    public void CachedCoverageKeepsAnExactSpatialMapAcrossRepeatedEvaluation()
    {
        const string expected = "##.##..." + ".###...#" + "##.....#" + "#.#..#.."
            + ".#.#.#.." + ".###.###" + "#######." + ".###....";
        for (var repeat = 0; repeat < 3; repeat++) {
            var pixels = new char[64];
            for (uint y = 0; y < 8; y++)
                for (uint x = 0; x < 8; x++)
                    pixels[y * 8 + x] = PbrShadowCoverage.Covers(.5f,
                        PbrShadowCoverage.StableHash(x, y, 3, 17, 42)) ? '#' : '.';
            Assert.Equal(expected, new string(pixels));
        }
    }

    [Theory]
    [InlineData(.125f)]
    [InlineData(.25f)]
    [InlineData(.5f)]
    [InlineData(.75f)]
    [InlineData(.875f)]
    public void FractionalAlphaProducesFractionalCoverageRatherThanAFixedCutoff(float alpha)
    {
        var covered = 0;
        for (uint y = 0; y < TileSize; y++)
            for (uint x = 0; x < TileSize; x++)
                if (PbrShadowCoverage.Covers(alpha, PbrShadowCoverage.StableHash(x, y, 3, 17, 42))) covered++;
        // Admission is measured over geometry samples, independently of any
        // individual hash vector. A single fixed cutoff gives only 0 or 100%.
        Assert.InRange((double)covered / Samples, alpha - .015, alpha + .015);
    }

    [Fact]
    public void CoverageIsNestedWhenOnlyTheMaterialOpacityIncreases()
    {
        for (uint y = 0; y < TileSize; y++)
            for (uint x = 0; x < TileSize; x++) {
                var hash = PbrShadowCoverage.StableHash(x, y, 3, 17, 42);
                var quarter = PbrShadowCoverage.Covers(.25f, hash);
                var half = PbrShadowCoverage.Covers(.5f, hash);
                var threeQuarters = PbrShadowCoverage.Covers(.75f, hash);
                Assert.False(quarter && !half);
                Assert.False(half && !threeQuarters);
            }
    }

    [Theory]
    [InlineData(.25f)]
    [InlineData(.5f)]
    [InlineData(.75f)]
    public void IndependentOverlappingInstancesRetainProductTransmittance(float alpha)
    {
        var transmitted = 0;
        var bothCovered = 0;
        for (uint y = 0; y < TileSize; y++)
            for (uint x = 0; x < TileSize; x++) {
                var first = PbrShadowCoverage.Covers(alpha, PbrShadowCoverage.StableHash(x, y, 3, 17, 42));
                var second = PbrShadowCoverage.Covers(alpha, PbrShadowCoverage.StableHash(x, y, 3, 18, 42));
                if (!first && !second) transmitted++;
                if (first && second) bothCovered++;
            }
        var expectedTransmission = (1 - (double)alpha) * (1 - alpha);
        Assert.InRange((double)transmitted / Samples, expectedTransmission - .015, expectedTransmission + .015);
        var expectedBoth = (double)alpha * alpha;
        Assert.InRange((double)bothCovered / Samples, expectedBoth - .015, expectedBoth + .015);
    }

    [Theory]
    [InlineData(0ul, 0ul, 0ul, 0ul, false)]
    [InlineData(4ul, 4ul, 9ul, 9ul, false)]
    [InlineData(4ul, 5ul, 9ul, 9ul, true)]
    [InlineData(4ul, 4ul, 9ul, 10ul, true)]
    [InlineData(4ul, 5ul, 9ul, 10ul, true)]
    [InlineData(ulong.MaxValue, 0ul, 9ul, 9ul, true)]
    [InlineData(4ul, 4ul, ulong.MaxValue, 0ul, true)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, false)]
    public void PoseOrAlphaPublicationChangesInvalidateOtherwiseReusableDepth(
        ulong previousPose, ulong pose, ulong previousAlpha, ulong alpha, bool expected)
        => Assert.Equal(expected, PbrShadowCoverage.NeedsRefresh(previousPose, pose, previousAlpha, alpha));
}
