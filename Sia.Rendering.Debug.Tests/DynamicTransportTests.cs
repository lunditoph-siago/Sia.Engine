using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class DynamicTransportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(33)]
    public void MaximumCapacityCoversUnsharedVerticesAndMaterials(int count)
    {
        var triangles = Enumerable.Range(0, count).Select(i => new SceneTraceTriangle(
            new(i * 4, 0, 0), new(i * 4 + 1, 0, 0), new(i * 4, 1, 0),
            new(i + 1), new(i), true)).ToArray();
        var capacity = SceneTraceData.MaximumPackedBytes(count);
        var trace = new SceneTraceData(triangles, capacity);
        Assert.Equal(capacity, (ulong)trace.Packed.Length * 16);
        Assert.Throws<ArgumentException>(() => new SceneTraceData(triangles, capacity - 16));
    }

    [Fact]
    public void DynamicTraceUsesLiveWorldTransformWithoutChangingStaticBakeDomain()
    {
        var mesh = MeshPatchAsset.Cook(new([
            new(new(0, 0, 0), new(0, 0, 1), new(0)),
            new(new(1, 0, 0), new(0, 0, 1), new(0)),
            new(new(0, 1, 0), new(0, 0, 1), new(0))], [0, 1, 2], new(new(0), new(1))));
        PbrMaterialAsset[] materials = [new(new() { BaseColor = new(.5f) }), new(new(), AlphaBlend: true)];
        PbrSceneInstance[] slots = [new(0, 0, float4x4.identity),
            new(0, 0, float4x4.identity) { Dynamic = true }, new(0, 1, float4x4.identity) { Dynamic = true }];
        var source = PbrSceneAsset.Create([mesh], materials, slots);
        var identity = PbrSceneTransport.StaticIdentity(source);
        var capacity = PbrSceneTransport.DynamicMaximumBytes(source);
        Assert.Equal(SceneTraceData.MaximumPackedBytes(1), capacity);
        var moved = slots[1] with { Transform = new(new(0, 2, 0, 0), new(-3, 0, 0, 0), new(0, 0, .5f, 0), new(4, 5, 6, 1)) };
        var actual = PbrSceneTransport.BuildDynamic(source, [moved], capacity)!;
        // Independent world-space triangle, including rotation and non-uniform scale.
        var expected = new SceneTraceData([new(new(4, 5, 6), new(4, 7, 6), new(1, 5, 6), new(.5f), new(0), false)], capacity);
        Assert.Equal(expected.Packed.ToArray(), actual.Packed.ToArray());
        slots[1] = moved;
        Assert.Equal(identity, PbrSceneTransport.StaticIdentity(PbrSceneAsset.Create([mesh], materials, slots)));
        Assert.Null(PbrSceneTransport.BuildDynamic(source, [], capacity));
        Assert.Throws<ArgumentException>(() => PbrSceneTransport.BuildDynamic(source, [slots[0]], capacity));
        Assert.Throws<ArgumentException>(() => PbrSceneTransport.BuildDynamic(source, [slots[2]], capacity));
    }
}
