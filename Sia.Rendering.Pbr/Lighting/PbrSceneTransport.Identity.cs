using System.Buffers;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    private static void AppendFinestIdentity(SceneIdentityHash hash, MeshPatchTree tree, Span<float4> positions)
    {
        var remap = ArrayPool<int>.Shared.Rent(tree.VertexCount);
        int[]? source = null;
        try {
            source = ArrayPool<int>.Shared.Rent(tree.VertexCount);
            remap.AsSpan(0, tree.VertexCount).Fill(-1);
            var vertexCount = 0;
            var triangleCount = 0;
            foreach (var node in tree.Nodes.Span) {
                if (node.ChildCount != 0) continue;
                triangleCount = checked(triangleCount + node.TriangleCount);
                foreach (var cluster in tree.Meshlets.Slice(node.MeshletOffset, node.MeshletCount)) {
                    foreach (var vertex in tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount)) {
                        if (remap[vertex] >= 0) continue;
                        remap[vertex] = vertexCount;
                        source[vertexCount++] = (int)vertex;
                    }
                }
            }
            Span<float4> header = [new(vertexCount, checked(triangleCount * 3), tree.RootCount, 0)];
            hash.AppendData(MemoryMarshal.AsBytes(header));
            for (var offset = 0; offset < vertexCount; offset += positions.Length) {
                var count = System.Math.Min(positions.Length, vertexCount - offset);
                for (var i = 0; i < count; i++)
                    positions[i] = new(tree.Vertices[source[offset + i]].Position, 0);
                hash.AppendData(MemoryMarshal.AsBytes(positions[..count]));
            }
            Span<uint> indices = stackalloc uint[1024];
            var buffered = 0;
            foreach (var node in tree.Nodes.Span) {
                if (node.ChildCount != 0) continue;
                foreach (var cluster in tree.Meshlets.Slice(node.MeshletOffset, node.MeshletCount)) {
                    var vertices = tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount);
                    foreach (var local in tree.MeshletTriangleIndices.Slice(cluster.TriangleOffset, cluster.TriangleCount * 3)) {
                        indices[buffered++] = (uint)remap[vertices[local]];
                        if (buffered != indices.Length) continue;
                        hash.AppendData(MemoryMarshal.AsBytes(indices));
                        buffered = 0;
                    }
                }
            }
            hash.AppendData(MemoryMarshal.AsBytes(indices[..buffered]));
        }
        finally {
            if (source is not null) ArrayPool<int>.Shared.Return(source);
            ArrayPool<int>.Shared.Return(remap);
        }
    }
}
