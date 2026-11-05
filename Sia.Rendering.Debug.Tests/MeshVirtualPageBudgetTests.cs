using System.Runtime.CompilerServices;
using Sia.Engine.Mesh;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class MeshVirtualPageBudgetTests
{
    private static MeshData Quad()
    {
        float3 normal = new(0, 0, 1);
        return new([
            new(new(0, 0, 0), normal, new(0, 0)),
            new(new(1, 0, 0), normal, new(1, 0)),
            new(new(1, 1, 0), normal, new(1, 1)),
            new(new(0, 1, 0), normal, new(0, 1))
        ], [0, 1, 2, 0, 2, 3], new(new(0, 0, 0), new(1, 1, 0))) {
            BakeUvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]
        };
    }

    [Fact]
    public void VirtualPageCreationEnforcesRetainedBakeUvBytes()
    {
        var mesh = Quad();
        var page = MeshVirtualPageData.Create(mesh.Vertices, mesh.Indices, mesh.BakeUvs);
        var retained = RetainedBytes(mesh);

        Assert.Equal(321, page.EncodedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeshVirtualPageData.Create(mesh.Vertices, mesh.Indices, mesh.BakeUvs, retained - 1));
        Assert.Equal(retained, MeshVirtualPageData.Create(mesh.Vertices, mesh.Indices, mesh.BakeUvs, retained).PayloadBytes);
    }

    [Fact]
    public void VirtualPageLoadEnforcesRetainedBakeUvBytesIndependentlyOfWireSize()
    {
        var mesh = Quad();
        var page = MeshVirtualPageData.Create(mesh.Vertices, mesh.Indices, mesh.BakeUvs);
        using var encoded = new MemoryStream();
        page.Save(encoded);
        var retained = RetainedBytes(mesh);

        encoded.Position = 0;
        Assert.Throws<InvalidDataException>(() => MeshVirtualPageData.Load(encoded, retained - 1));
        encoded.Position = 0;
        var decoded = MeshVirtualPageData.Load(encoded, retained);
        Assert.Equal(retained, decoded.PayloadBytes);
        Assert.Equal(page.ContentHash, decoded.ContentHash);
    }

    [Fact]
    public void VirtualHierarchyCountsTheSameRetainedBakeUvStride()
    {
        var mesh = Quad();
        var baked = MeshPatchTree.Create([new(mesh, 0, [])]);
        var unbaked = MeshPatchTree.Create([new(mesh with { BakeUvs = [] }, 0, [])]);

        Assert.Equal(unbaked.VertexCount, baked.VertexCount);
        Assert.Equal((ulong)baked.VertexCount * (ulong)Unsafe.SizeOf<float2>(), baked.PayloadBytes - unbaked.PayloadBytes);
    }

    private static ulong RetainedBytes(MeshData mesh) => (ulong)mesh.Vertices.Length * (ulong)Unsafe.SizeOf<MeshVertex>()
        + (ulong)mesh.BakeUvs.Length * (ulong)Unsafe.SizeOf<float2>() + (ulong)mesh.Indices.Length * sizeof(uint);
}
