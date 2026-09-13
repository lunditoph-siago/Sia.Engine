using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class MeshletRasterDataTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TreeAssemblyPreservesCombinedRasterStreams(int count)
    {
        var trees = Enumerable.Range(0, count).Select(i => MeshPatchTree.Create(
            [new(ProceduralMesh.Cube(size: i + 1), 0, [])], maxVertices: 8, maxTriangles: 4)).ToArray();
        var separate = trees.Select(tree => {
            var (geometry, meshlets) = tree.CopyGeometry();
            return MeshletRasterData.Create(geometry, meshlets);
        }).ToArray();
        var expected = MeshletRasterData.Combine(separate);
        var actual = MeshletRasterData.Create(trees);
        Assert.Equal(expected.Vertices.ToArray(), actual.Vertices.ToArray());
        Assert.Equal(expected.Meshlets.ToArray(), actual.Meshlets.ToArray());
        Assert.Equal(expected.Indices.ToArray(), actual.Indices.ToArray());
        Assert.Equal(expected.Triangles.ToArray(), actual.Triangles.ToArray());
    }

    [Fact]
    public void TreeAssemblyAllocatesOnlyFinalSceneStorage()
    {
        var tree = MeshPatchTree.Create([new(ProceduralMesh.Sphere(), 0, [])]);
        var trees = Enumerable.Repeat(tree, 32).ToArray();
        _ = MeshletRasterData.Create(trees);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var raster = MeshletRasterData.Create(trees);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var storage = (long)raster.Vertices.Length * Marshal.SizeOf<MeshVertex>()
            + (long)raster.Meshlets.Length * 16 + (long)raster.Indices.Length * 4 + (long)raster.Triangles.Length * 16;
        Assert.InRange(allocated, storage, storage + 4096);
        GC.KeepAlive(raster);
    }

    [Fact]
    public void NonFiniteAttributesCannotEnterRasterAssembly()
    {
        var mesh = ProceduralMesh.Cube();
        mesh.Vertices[0] = mesh.Vertices[0] with { UV = new(float.NaN, 0) };
        var meshlets = MeshletBuilder.Build(mesh);
        Assert.Throws<ArgumentException>(() => MeshletRasterData.Create(mesh, meshlets));
        Assert.Throws<ArgumentException>(() => MeshPatchTree.Create([new(mesh, 0, [])]));
    }

    [Fact]
    public void CallerOwnedMeshletsStillRejectInvalidTopology()
    {
        var mesh = ProceduralMesh.Cube();
        var meshlets = MeshletBuilder.Build(mesh);
        meshlets.SourceTriangleIndices[1] = meshlets.SourceTriangleIndices[0];
        Assert.Throws<ArgumentException>(() => MeshletRasterData.Create(mesh, meshlets));
    }
}
