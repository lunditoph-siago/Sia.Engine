using System.Buffers;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed class PbrResidentGeometry : IDisposable
{
    internal delegate void IndexUpload(ReadOnlySpan<uint> values, int first);

    private readonly MeshPatchTree _tree;
    private int[]? _remap;
    private int[]? _source;

    public int VertexCount { get; }
    public uint Fine { get; }
    public uint Coarse { get; }
    public float Error { get; }
    public Aabb Bounds => _tree.Bounds;

    public PbrResidentGeometry(MeshPatchTree tree)
    {
        _tree = tree;
        Fine = (uint)tree.FinestTriangleCount;
        if (tree.RootCount != tree.Nodes.Length) {
            VertexCount = tree.VertexCount;
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                Coarse = checked(Coarse + (uint)node.TriangleCount);
                Error = MathF.Max(Error, node.EstimatedSpatialError);
            }
            return;
        }
        try {
            _remap = ArrayPool<int>.Shared.Rent(tree.VertexCount);
            _source = ArrayPool<int>.Shared.Rent(tree.VertexCount);
            _remap.AsSpan(0, tree.VertexCount).Fill(-1);
            foreach (var cluster in tree.Meshlets) {
                foreach (var vertex in tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount)) {
                    if (_remap[vertex] >= 0) continue;
                    _remap[vertex] = VertexCount;
                    _source[VertexCount++] = (int)vertex;
                }
            }
        }
        catch {
            Dispose();
            throw;
        }
    }

    public void PackVertices(Span<float4> packed, int first, int count, float4x4 transform,
        float4x4 normal, int material, int planeStride = 0)
    {
        var stride = planeStride == 0 ? count : planeStride;
        if (first < 0 || count < 0 || first > VertexCount - count || stride < count
            || packed.Length < checked(stride * 2 + count))
            throw new ArgumentOutOfRangeException(nameof(count));
        var vertices = _tree.Vertices;
        for (var v = 0; v < count; v++) {
            var vertex = vertices[_source is null ? first + v : _source[first + v]];
            var position = math.mul(transform, new float4(vertex.Position, 1)).xyz;
            var n = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
            var t = math.mul(transform, new float4(vertex.Tangent.xyz, 0)).xyz;
            packed[v] = new(position, n.x);
            packed[stride + v] = new(n.y, n.z, vertex.UV.x, vertex.UV.y);
            packed[stride * 2 + v] = new(t, (vertex.Tangent.w < 0 ? -1 : 1) * (material + 1));
        }
    }

    public void UploadIndices(Span<uint> scratch, uint vertexOffset, IndexUpload upload)
    {
        if (scratch.IsEmpty) throw new ArgumentException("Index scratch must be nonempty.", nameof(scratch));
        var writer = new IndexWriter(scratch, upload);
        WriteIndices(ref writer, vertexOffset);
        writer.Flush();
    }

    public void WriteIndices(ref IndexWriter writer, uint vertexOffset)
    {
        if (_remap is not null) {
            foreach (var cluster in _tree.Meshlets) {
                var vertices = _tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount);
                foreach (var local in _tree.MeshletTriangleIndices.Slice(cluster.TriangleOffset, cluster.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + (uint)_remap[vertices[local]]));
            }
        }
        else {
            foreach (var node in _tree.Nodes.Span) {
                if (node.ChildCount != 0) continue;
                foreach (var index in _tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + index));
            }
            foreach (var node in _tree.Nodes.Span[.._tree.RootCount]) {
                foreach (var index in _tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + index));
            }
        }
    }

    public void Dispose()
    {
        if (_source is not null) ArrayPool<int>.Shared.Return(_source);
        if (_remap is not null) ArrayPool<int>.Shared.Return(_remap);
        _source = _remap = null;
    }

    internal ref struct IndexWriter(Span<uint> scratch, IndexUpload upload)
    {
        private readonly Span<uint> _scratch = scratch;
        private readonly IndexUpload _upload = upload;
        private int _count;
        private int _first;

        public void Append(uint value)
        {
            _scratch[_count++] = value;
            if (_count == _scratch.Length) Flush();
        }

        public void Flush()
        {
            if (_count == 0) return;
            _upload(_scratch[.._count], _first);
            _first = checked(_first + _count);
            _count = 0;
        }
    }
}
