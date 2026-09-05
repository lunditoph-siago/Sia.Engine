using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed class MeshletRasterData
{
    public ReadOnlyMemory<MeshVertex> Vertices { get; }
    public ReadOnlyMemory<uint4> Meshlets { get; }
    public ReadOnlyMemory<uint> Indices { get; }
    public ReadOnlyMemory<uint4> Triangles { get; }

    private MeshletRasterData(MeshVertex[] vertices, uint4[] meshlets, uint[] indices, uint4[] triangles)
    {
        Vertices = vertices;
        Meshlets = meshlets;
        Indices = indices;
        Triangles = triangles;
    }

    public static MeshletRasterData Create(MeshData mesh, MeshletData meshlets)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(meshlets);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        ArgumentNullException.ThrowIfNull(meshlets.Meshlets);
        ArgumentNullException.ThrowIfNull(meshlets.VertexIndices);
        ArgumentNullException.ThrowIfNull(meshlets.TriangleIndices);
        ArgumentNullException.ThrowIfNull(meshlets.SourceTriangleIndices);
        if (mesh.Indices.Length % 3 != 0 || meshlets.TriangleIndices.Length != mesh.Indices.Length
            || meshlets.SourceTriangleIndices.Length != mesh.Indices.Length / 3) {
            throw new ArgumentException("Meshlets must cover all source triangles.", nameof(meshlets));
        }
        foreach (var vertex in mesh.Vertices) {
            if (!Finite(vertex.Position) || !Finite(vertex.Normal)
                || !float.IsFinite(vertex.UV.x) || !float.IsFinite(vertex.UV.y)) {
                throw new ArgumentException("Raster vertex attributes must be finite.", nameof(mesh));
            }
        }
        var descriptors = new uint4[meshlets.Meshlets.Length];
        var triangles = new uint4[mesh.Indices.Length / 3];
        var seen = new bool[triangles.Length];
        var indices = new uint[checked(meshlets.VertexIndices.Length + triangles.Length)];
        meshlets.VertexIndices.CopyTo(indices, 0);
        foreach (var index in meshlets.VertexIndices) {
            if (index >= mesh.Vertices.Length) {
                throw new ArgumentException("Meshlet vertex reference is out of range.", nameof(meshlets));
            }
        }
        var nextVertex = 0;
        var nextTriangle = 0;
        for (var m = 0; m < descriptors.Length; m++) {
            var cluster = meshlets.Meshlets[m];
            if (cluster.VertexOffset != nextVertex || cluster.TriangleOffset != nextTriangle * 3
                || cluster.VertexCount is <= 0 or > 256 || cluster.TriangleCount <= 0
                || cluster.VertexCount > meshlets.VertexIndices.Length - nextVertex
                || cluster.TriangleCount > triangles.Length - nextTriangle) {
                throw new ArgumentException("Meshlet ranges must form a complete, non-overlapping stream.", nameof(meshlets));
            }
            descriptors[m] = new uint4((uint)nextVertex,
                (uint)(meshlets.VertexIndices.Length + nextTriangle), (uint)cluster.TriangleCount, 0);
            for (var t = 0; t < cluster.TriangleCount; t++) {
                var ordinal = nextTriangle + t;
                var source = meshlets.SourceTriangleIndices[ordinal];
                if (source >= seen.Length || seen[source]) {
                    throw new ArgumentException("Source triangles must be covered exactly once.", nameof(meshlets));
                }
                seen[source] = true;
                uint packed = 0;
                for (var corner = 0; corner < 3; corner++) {
                    var local = meshlets.TriangleIndices[ordinal * 3 + corner];
                    if (local >= cluster.VertexCount
                        || indices[nextVertex + local] != mesh.Indices[(int)source * 3 + corner]) {
                        throw new ArgumentException("Meshlet topology differs from the source mesh.", nameof(meshlets));
                    }
                    packed |= (uint)local << (corner * 8);
                }
                indices[meshlets.VertexIndices.Length + ordinal] = packed;
                triangles[ordinal] = new uint4((uint)m, (uint)t, 0, 0);
            }
            nextVertex += cluster.VertexCount;
            nextTriangle += cluster.TriangleCount;
        }
        if (nextVertex != meshlets.VertexIndices.Length || nextTriangle != triangles.Length) {
            throw new ArgumentException("Meshlet ranges do not cover their streams.", nameof(meshlets));
        }
        return new(mesh.Vertices.ToArray(), descriptors, indices, triangles);
    }

    private static bool Finite(float3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
}
