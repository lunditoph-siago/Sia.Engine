namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchTree
{
    internal MeshPatchTree ExtractFinest()
    {
        var (geometry, meshlets) = CopyFinestGeometry();
        var count = 0;
        foreach (var node in Nodes.Span) { if (node.ChildCount == 0) { count++; } }
        var nodes = new MeshPatchNode[count];
        int next = 0, triangle = 0, meshlet = 0;
        foreach (var node in Nodes.Span) {
            if (node.ChildCount != 0) { continue; }
            nodes[next++] = node with { Parent = -1, ChildOffset = nodes.Length, TriangleOffset = triangle, MeshletOffset = meshlet };
            triangle += node.TriangleCount;
            meshlet += node.MeshletCount;
        }
        return new(nodes, nodes.Length, FinestTriangleCount, geometry, meshlets);
    }

    public (MeshData Geometry, MeshletData Meshlets) CopyFinestGeometry()
    {
        var remap = new int[_geometry.Vertices.Length];
        Array.Fill(remap, -1);
        int vertexCount = 0, meshletCount = 0, referenceCount = 0;
        foreach (var node in Nodes.Span) {
            if (node.ChildCount != 0) { continue; }
            meshletCount = checked(meshletCount + node.MeshletCount);
            foreach (var cluster in _meshlets.Meshlets.AsSpan(node.MeshletOffset, node.MeshletCount)) {
                referenceCount = checked(referenceCount + cluster.VertexCount);
                foreach (var vertex in _meshlets.VertexIndices.AsSpan(cluster.VertexOffset, cluster.VertexCount)) {
                    if (remap[vertex] < 0) { remap[vertex] = vertexCount++; }
                }
            }
        }
        var vertices = new MeshVertex[vertexCount];
        for (var i = 0; i < remap.Length; i++) { if (remap[i] >= 0) { vertices[remap[i]] = _geometry.Vertices[i]; } }
        var indices = new uint[checked(FinestTriangleCount * 3)];
        var clusters = new Meshlet[meshletCount];
        var references = new uint[referenceCount];
        var localIndices = new byte[indices.Length];
        var sources = new uint[FinestTriangleCount];
        int meshletOffset = 0, vertexOffset = 0, triangleOffset = 0;
        foreach (var node in Nodes.Span) {
            if (node.ChildCount != 0) { continue; }
            foreach (var cluster in _meshlets.Meshlets.AsSpan(node.MeshletOffset, node.MeshletCount)) {
                clusters[meshletOffset++] = cluster with { VertexOffset = vertexOffset, TriangleOffset = triangleOffset * 3 };
                for (var v = 0; v < cluster.VertexCount; v++) {
                    references[vertexOffset + v] = (uint)remap[_meshlets.VertexIndices[cluster.VertexOffset + v]];
                }
                var local = _meshlets.TriangleIndices.AsSpan(cluster.TriangleOffset, cluster.TriangleCount * 3);
                local.CopyTo(localIndices.AsSpan(triangleOffset * 3));
                for (var i = 0; i < local.Length; i++) { indices[triangleOffset * 3 + i] = references[vertexOffset + local[i]]; }
                for (var t = 0; t < cluster.TriangleCount; t++) { sources[triangleOffset + t] = (uint)(triangleOffset + t); }
                vertexOffset += cluster.VertexCount;
                triangleOffset += cluster.TriangleCount;
            }
        }
        return (new(vertices, indices, _geometry.Bounds), new(clusters, references, localIndices, sources));
    }
}
