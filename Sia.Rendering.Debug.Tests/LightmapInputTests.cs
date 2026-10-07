using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapInputTests
{
    private static MeshPatchAsset Quad(float3? normal = null)
    {
        var n = normal ?? new float3(0, 0, 1);
        MeshVertex[] vertices = [new(new(0, 0, 0), n, new(0, 0)), new(new(1, 0, 0), n, new(1, 0)),
            new(new(1, 1, 0), n, new(1, 1)), new(new(0, 1, 0), n, new(0, 1))];
        return MeshPatchAsset.Cook(new(vertices, [0, 1, 2, 0, 2, 3], new(float3.zero, new(1, 1, 0))));
    }

    private static PbrMaterialAsset Material(bool transparent = false)
        => new(new() { BaseColor = new(1), Roughness = .8f }, AlphaBlend: transparent);

    private static float4x4 Translate(float x) => new(new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(x, 0, 0, 1));

    [Fact]
    public void StaticReceiversHaveDistinctTilesAndCorrectWorldSurfaceFrames()
    {
        Assert.Equal(PbrLightmapTexel.Stride, Marshal.SizeOf<PbrLightmapTexel>());
        Assert.False(default(PbrLightmapTexel).Covered);
        var transform = new float4x4(new(2, 0, 0, 0), new(0, 3, 0, 0), new(0, 0, 4, 0), new(10, -5, 7, 1));
        var original = PbrSceneAsset.Create([Quad(new(1, 0, 1))], [Material()],
            [new(0, 0, transform), new(0, 0, Translate(-10))]);
        var input = PbrLightmapInput.Create(original, 32);
        Assert.Equal(64, input.Resolution);
        Assert.Equal(2, input.Receivers.Length);
        Assert.False(original.Geometry.Span[0].HasLightmapUV);
        Assert.True(input.Scene.Geometry.Span[0].HasLightmapUV);
        Assert.True(PbrSceneAsset.Decode(input.Scene.Encode()).Geometry.Span[0].HasLightmapUV);
        var a = input.Receivers.Span[0];
        var b = input.Receivers.Span[1];
        Assert.Equal(new float4(.5f, .5f, 0, 0), a.ScaleBias);
        Assert.Equal(new float4(.5f, .5f, .5f, 0), b.ScaleBias);
        Assert.Equal(input.CoveredTexelCount, input.Texels.ToArray().Count(t => t.Covered));
        Assert.Equal(28 * 28 * 2, input.CoveredTexelCount);
        var corner = input.Texels.Span[2 * input.Resolution + 2];
        Assert.True(corner.Covered);
        Assert.Equal(10, corner.Position.x, 4);
        Assert.Equal(-5, corner.Position.y, 4);
        Assert.Equal(7, corner.Position.z, 4);
        var expected = math.normalize(new float3(.5f, 0, .25f));
        foreach (var texel in input.Texels.Span) {
            if (!texel.Covered) continue;
            Assert.InRange(math.length(texel.Normal), .99999f, 1.00001f);
            if (texel.StaticInstance == 0) {
                Assert.InRange(texel.Position.x, 9.9999f, 12.0001f);
                Assert.InRange(texel.Position.y, -5.0001f, -1.9999f);
                Assert.Equal(7, texel.Position.z, 5);
                Assert.InRange(math.length(texel.Normal - expected), 0, 1e-5f);
            } else {
                Assert.Equal(1, texel.StaticInstance);
                Assert.InRange(texel.Position.x, -10.0001f, -8.9999f);
                Assert.InRange(texel.Position.y, -.0001f, 1.0001f);
            }
        }
        // Filtering border points at an actual covered sample, without inventing a ray origin.
        Assert.False(input.Texels.Span[0].Covered);
        Assert.True(input.Texels.Span[input.FilterSources.Span[0]].Covered);
        Assert.Equal(0, input.Texels.Span[input.FilterSources.Span[0]].StaticInstance);
        Assert.Equal(1, input.Texels.Span[input.FilterSources.Span[32]].StaticInstance);
        Assert.Equal(-1, input.FilterSources.Span[32 * input.Resolution]); // Unused tile.
        for (var i = 0; i < input.Texels.Length; i++) {
            var source = input.FilterSources.Span[i];
            if (source < 0) continue;
            Assert.True(input.Texels.Span[source].Covered);
            var x = i % input.Resolution;
            var y = i / input.Resolution;
            var receiver = x < 32 ? a : b;
            Assert.True(y < 32);
            Assert.Equal(receiver.StaticInstance, input.Texels.Span[source].StaticInstance);
            Assert.InRange(source % input.Resolution, receiver.X, receiver.X + 31);
            Assert.InRange(source / input.Resolution, receiver.Y, receiver.Y + 31);
        }
    }

    [Fact]
    public void DynamicSlotChangesDoNotInvalidateCanonicalSurfaceIdentity()
    {
        var geometry = Quad();
        var scene = PbrSceneAsset.Create([geometry], [Material(), Material(true)],
            [new(0, 0, Translate(0)), new(0, 1, Translate(5)), new(0, 0, Translate(2))]);
        var initial = PbrLightmapInput.Create(scene, 16);
        var moved = PbrSceneAsset.Create([Quad(new(0, 1, 1)), geometry], [Material(true), Material()],
            [new(0, 1, Translate(-4)) { Dynamic = true }, new(1, 1, Translate(0)),
                new(1, 0, Translate(5)), new(0, 1, Translate(8)) { Dynamic = true }, new(1, 1, Translate(2))]);
        var input = PbrLightmapInput.Create(moved, 16);
        Assert.Equal(new[] { 1, 4 }, input.Receivers.ToArray().Select(r => r.SourceInstance));
        Assert.Equal(new[] { 0, 2 }, input.Receivers.ToArray().Select(r => r.StaticInstance));
        Assert.True(initial.SurfaceIdentity.Span.SequenceEqual(input.SurfaceIdentity.Span));
        Assert.True(initial.Texels.Span.SequenceEqual(input.Texels.Span));
        Assert.True(initial.FilterSources.Span.SequenceEqual(input.FilterSources.Span));
        Assert.False(input.Scene.Geometry.Span[0].HasLightmapUV); // Dynamic-only geometry was not recooked.
        Assert.True(input.Scene.Instances.Span[0].Dynamic);
        var changed = PbrSceneAsset.Create([Quad(new(1, 0, 1))], scene.Materials.Span, scene.Instances.Span);
        Assert.False(initial.SurfaceIdentity.Span.SequenceEqual(PbrLightmapInput.Create(changed, 16).SurfaceIdentity.Span));
        Assert.False(initial.SurfaceIdentity.Span.SequenceEqual(PbrLightmapInput.Create(scene, 32).SurfaceIdentity.Span));
        Assert.False(initial.SurfaceIdentity.Span.SequenceEqual(PbrLightmapInput.Create(scene, 16, 1).SurfaceIdentity.Span));
    }

    [Fact]
    public void HardEdgeChartDilationNeverCrossesChartOwnership()
    {
        MeshVertex[] v = [new(new(0, 0, 0), new(0, 0, 1), default), new(new(1, 0, 0), new(0, 0, 1), default),
            new(new(0, 1, 0), new(0, 0, 1), default), new(new(0, 0, 1), new(0, -1, 0), default)];
        var mesh = MeshPatchAsset.Cook(new(v, [0, 1, 2, 1, 0, 3], new(float3.zero, new(1, 1, 1))));
        var scene = PbrSceneAsset.Create([mesh], [Material()], [new(0, 0, Translate(0))]);
        var input = PbrLightmapInput.Create(scene, 32);
        Assert.Equal(new[] { 0, 1 }, input.Texels.ToArray().Where(t => t.Covered).Select(t => t.Chart).Distinct().Order());
        // Equal projected triangles have 16-pixel-wide allocations: each filtering address
        // must stay in the half containing its own chart, including the empty seam gutter.
        for (var i = 0; i < input.FilterSources.Length; i++) {
            var source = input.FilterSources.Span[i];
            if (source < 0) continue;
            Assert.Equal(i % 32 < 16 ? 0 : 1, input.Texels.Span[source].Chart);
        }
    }

    [Fact]
    public void SubpixelCurvedChartUsesRealSurfaceSamplesAndKeepsItsOwnFilteringBorder()
    {
        // Tiny source-UV diamond beside a larger planar chart. None of the four
        // bounding-box corner centers lies inside the diamond, even as resolution
        // increases while physical density remains shared with the larger chart.
        float3[] p = [new(10, .43f, 2), new(10.57f, 0, 2.2f), new(11, .43f, 2), new(10.43f, 1, 2)];
        float2[] uv = [new(0, .00043f), new(.00057f, 0), new(.001f, .00043f), new(.00043f, .001f)];
        MeshVertex[] vertices = [new(new(0, 0, 0), new(0, 0, 1), default), new(new(1, 0, 0), new(0, 0, 1), default),
            new(new(1, 1, 0), new(0, 0, 1), default), new(new(0, 1, 0), new(0, 0, 1), default),
            .. p.Select((position, i) => new MeshVertex(position, new(0, 0, 1), uv[i]))];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var mesh = MeshPatchAsset.Cook(new(vertices, indices, new(float3.zero, new(11, 1, 2.2f))));
        var scene = PbrSceneAsset.Create([mesh], [Material()], [new(0, 0, Translate(3))]);
        var finest = mesh.ExtractFinest().Build.Tree;
        var input = PbrLightmapInput.Create(scene, 16);
        var thin = input.Texels.ToArray().Where(t => t.Covered && t.Position.x > 10).ToArray();
        Assert.NotEmpty(thin);
        Assert.Single(thin.Select(t => t.Chart).Distinct());
        foreach (var sample in thin) {
            var triangle = sample.Triangle;
            var a = finest.Vertices[(int)finest.Indices[triangle * 3]].Position + new float3(3, 0, 0);
            var b = finest.Vertices[(int)finest.Indices[triangle * 3 + 1]].Position + new float3(3, 0, 0);
            var c = finest.Vertices[(int)finest.Indices[triangle * 3 + 2]].Position + new float3(3, 0, 0);
            var n = math.normalize(math.cross(b - a, c - a));
            Assert.InRange(MathF.Abs(math.dot(n, sample.Position - a)), 0, 1e-5f);
            Assert.InRange(math.dot(math.cross(b - a, sample.Position - a), n), -1e-5f, float.MaxValue);
            Assert.InRange(math.dot(math.cross(c - b, sample.Position - b), n), -1e-5f, float.MaxValue);
            Assert.InRange(math.dot(math.cross(a - c, sample.Position - c), n), -1e-5f, float.MaxValue);
            Assert.InRange(math.length(sample.Normal), .99999f, 1.00001f);
        }
        var again = PbrLightmapInput.Create(scene, 16);
        Assert.True(input.Texels.Span.SequenceEqual(again.Texels.Span));
        Assert.True(input.FilterSources.Span.SequenceEqual(again.FilterSources.Span));
        var chart = thin[0].Chart;
        var filtered = input.FilterSources.ToArray().Where(i => i >= 0 && input.Texels.Span[i].Chart == chart).ToArray();
        Assert.True(filtered.Length > thin.Length);
        Assert.All(filtered, i => Assert.True(input.Texels.Span[i].Covered));
        Assert.Contains(input.FilterSources.ToArray(), i => i < 0);
    }

    [Fact]
    public void ReceiverDimensionsBudgetCancellationAndEmptyDomainAreRejected()
    {
        var mesh = Quad();
        var scene = PbrSceneAsset.Create([mesh], [Material()], [new(0, 0, Translate(0))]);
        const ulong exactBytes = 16 * 16 * (PbrLightmapTexel.Stride + 4);
        Assert.Throws<InvalidOperationException>(() => PbrLightmapInput.Create(scene, 16, maximumBytes: exactBytes - 1));
        Assert.Equal(256, PbrLightmapInput.Create(scene, 16, maximumBytes: exactBytes).Texels.Length);
        Assert.Throws<InvalidOperationException>(() => PbrLightmapInput.Create(scene, 32, maximumAtlasResolution: 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrLightmapInput.Create(scene, 4));
        Assert.Throws<OperationCanceledException>(() => PbrLightmapInput.Create(scene, cancellationToken: new(true)));
        var dynamicScene = PbrSceneAsset.Create([mesh], [Material()], [new(0, 0, Translate(0)) { Dynamic = true }]);
        Assert.Throws<InvalidOperationException>(() => PbrLightmapInput.Create(dynamicScene));
        var transparentScene = PbrSceneAsset.Create([mesh], [Material(true)], [new(0, 0, Translate(0))]);
        Assert.Throws<InvalidOperationException>(() => PbrLightmapInput.Create(transparentScene));
        Assert.False(scene.Geometry.Span[0].HasLightmapUV);
    }
}
