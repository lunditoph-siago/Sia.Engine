using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class IblEnvironmentDataTests
{
    [Fact]
    public void FixedBrdfTableMatchesReferenceIntegration()
        => Assert.Equal(IblEnvironmentData.GenerateBrdfLut(), IblEnvironmentData.BrdfLut.ToArray());

    [Theory]
    [InlineData(1f)]
    [InlineData(65536f)]
    public void CachedProceduralEnvironmentPreservesReferenceFiltering(float intensity)
    {
        var sky = new ProceduralSky { SunDirection = new(.2f, .5f, .9f), Intensity = intensity };
        var data = IblEnvironmentData.Get(sky);
        Assert.Equal(IrradianceSh.Project(sky.Evaluate), data.Coefficients.ToArray());

        var scale = 1f;
        while (IblEnvironmentData.MaximumRadiance(sky) / scale > 32752) scale *= 2;
        Assert.Equal(scale, data.CubeRadianceScale);
        var offset = 0;
        for (var mip = 0; mip < IblEnvironmentAsset.MipCount; mip++) {
            var size = IblEnvironmentAsset.CubeSize >> mip;
            for (var face = 0; face < 6; face++) {
                foreach (var coordinate in new[] { 0, size / 2, size - 1 }) {
                    var expected = ReferencePixel(sky, mip, face, coordinate, coordinate) / scale;
                    var at = offset + (coordinate * size + coordinate) * 4;
                    Assert.Equal((Half)expected.x, data.Cube.Span[at]);
                    Assert.Equal((Half)expected.y, data.Cube.Span[at + 1]);
                    Assert.Equal((Half)expected.z, data.Cube.Span[at + 2]);
                    Assert.Equal((Half)1, data.Cube.Span[at + 3]);
                }
                offset += size * size * 4;
            }
        }
    }

    [Fact]
    public void CpuPayloadReuseIsExactAndBoundedToTwoSkyValues()
    {
        var first = new ProceduralSky { SunExponent = 253.25f };
        var second = first with { SunExponent = 253.5f };
        var third = first with { SunDirection = new(.2f, .5f, .9f) };
        var firstData = IblEnvironmentData.Get(first);
        var secondData = IblEnvironmentData.Get(second);
        Assert.Same(firstData, IblEnvironmentData.Get(first with { }));
        var thirdData = IblEnvironmentData.Get(third);
        Assert.NotSame(firstData, thirdData);
        Assert.NotSame(secondData, IblEnvironmentData.Get(second));

        var statistics = IblEnvironmentData.Statistics;
        Assert.Equal(2, statistics.CachedSkies);
        Assert.Equal(2 * (IblEnvironmentAsset.CubeTexels * 8L + 9 * 16L)
            + IblEnvironmentAsset.LutTexels * 8L, statistics.SharedCpuBytes);
        Assert.True(statistics.CacheHits > 0);
    }

    [Fact]
    public void InvalidOrUnrepresentableSkyDoesNotReplaceValidPayloads()
    {
        var sky = new ProceduralSky { SunExponent = 252.75f };
        var valid = IblEnvironmentData.Get(sky);
        var before = IblEnvironmentData.Statistics;
        Assert.Throws<ArgumentOutOfRangeException>(() => IblEnvironmentData.Get(sky with { SunExponent = float.NaN }));
        Assert.Throws<ArgumentException>(() => IblEnvironmentData.Get(sky with { Intensity = float.MaxValue }));
        Assert.Equal(before, IblEnvironmentData.Statistics);
        Assert.Same(valid, IblEnvironmentData.Get(sky));
    }

    // Frozen pre-optimization sampling formula verifies that hoisting the fixed
    // sample coordinates, tangent frame and sun normalization preserves pixels.
    private static float3 ReferencePixel(ProceduralSky sky, int mip, int face, int x, int y)
    {
        var size = IblEnvironmentAsset.CubeSize >> mip;
        var u = ((x + .5f) / size * 2) - 1;
        var v = ((y + .5f) / size * 2) - 1;
        var n = math.normalize(face switch {
            0 => new float3(1, -v, -u), 1 => new(-1, -v, u),
            2 => new(u, 1, v), 3 => new(u, -1, -v),
            4 => new(u, -v, 1), _ => new(-u, -v, -1)
        });
        var roughness = (float)mip / (IblEnvironmentAsset.MipCount - 1);
        var count = mip == 0 ? 1u : 256u;
        var color = float3.zero;
        float weight = 0;
        for (uint i = 0; i < count; i++) {
            var bits = i;
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555) << 1) | ((bits & 0xaaaaaaaa) >> 1);
            bits = ((bits & 0x33333333) << 2) | ((bits & 0xcccccccc) >> 2);
            bits = ((bits & 0x0f0f0f0f) << 4) | ((bits & 0xf0f0f0f0) >> 4);
            bits = ((bits & 0x00ff00ff) << 8) | ((bits & 0xff00ff00) >> 8);
            var sampleY = bits * 2.3283064365386963e-10f;
            var a = roughness * roughness;
            var phi = MathF.Tau * i / 256;
            var cosine = MathF.Sqrt((1 - sampleY) / (1 + (((a * a) - 1) * sampleY)));
            var sine = MathF.Sqrt(MathF.Max(0, 1 - (cosine * cosine)));
            var t = math.normalize(math.cross(MathF.Abs(n.z) < .999f ? new float3(0, 0, 1) : new(1, 0, 0), n));
            var h = math.normalize((t * (MathF.Cos(phi) * sine)) + (math.cross(n, t) * (MathF.Sin(phi) * sine)) + (n * cosine));
            var l = (2 * math.dot(n, h) * h) - n;
            var nl = MathF.Max(0, math.dot(n, l));
            color += sky.Evaluate(l) * nl;
            weight += nl;
        }
        return color / MathF.Max(weight, 1e-6f);
    }
}
