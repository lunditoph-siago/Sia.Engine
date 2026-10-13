using System.Buffers;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed class PbrResidentGeometry : IDisposable
{
    internal delegate void IndexUpload(ReadOnlySpan<uint> values, int first);

    private readonly MeshPatchTree? _tree;
    private readonly PbrRasterGeometry? _raster;
    private readonly PbrSceneStream.PageGeometry? _page;
    private readonly Dictionary<uint, uint>? _rasterRemap;
    private int[]? _remap;
    private int[]? _source;

    public int VertexCount { get; }
    public uint Fine { get; }
    public uint Coarse { get; }
    public float Error { get; }
    public Aabb Bounds { get; }

    public PbrResidentGeometry(MeshPatchTree tree)
    {
        _tree = tree;
        Bounds = tree.Bounds;
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

    public PbrResidentGeometry(PbrRasterGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _raster = geometry; Bounds = geometry.Bounds; Fine = (uint)(geometry.Indices.Length / 3);
        // Compact only vertices referenced by this section; never allocate a remap for the whole product per section.
        var capacity = System.Math.Min(geometry.Product.Vertices.Length, geometry.Indices.Length);
        _rasterRemap = new(System.Math.Min(capacity, 4096));
        try {
            foreach (var index in geometry.Indices) {
                if (!_rasterRemap.TryAdd(index, (uint)VertexCount)) continue;
                VertexCount++;
            }
            _source = ArrayPool<int>.Shared.Rent(VertexCount);
            foreach (var item in _rasterRemap) _source[item.Value] = (int)item.Key;
        }
        catch { Dispose(); throw; }
    }

    public PbrResidentGeometry(PbrSceneStream.PageGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry.Page);
        if (geometry.FirstTriangle < 0 || geometry.TriangleCount <= 0
            || geometry.FirstTriangle > geometry.Page.Indices.Length / 3 - geometry.TriangleCount)
            throw new ArgumentException("Page geometry requires a nonempty triangle range.", nameof(geometry));
        _page = geometry;
        Fine = (uint)geometry.TriangleCount;
        // A shared page may contain many unrelated node ranges. Transparent draws
        // own only their selected range, just as Raster draws own one section.
        try {
            _remap = ArrayPool<int>.Shared.Rent(geometry.Page.Vertices.Length);
            _remap.AsSpan(0, geometry.Page.Vertices.Length).Fill(-1);
            _source = ArrayPool<int>.Shared.Rent(System.Math.Min(geometry.Page.Vertices.Length, geometry.TriangleCount * 3));
            var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
            foreach (var index in geometry.Page.Indices.Slice(geometry.FirstTriangle * 3, geometry.TriangleCount * 3)) {
                if (_remap[index] >= 0) continue;
                _remap[index] = VertexCount; _source[VertexCount++] = (int)index;
                var position = geometry.Page.Vertices[(int)index].Position;
                minimum = math.min(minimum, position); maximum = math.max(maximum, position);
            }
            Bounds = new(minimum, maximum);
        }
        catch { Dispose(); throw; }
    }

    public void PackVertices(Span<float4> packed, int first, int count, float4x4 transform,
        float4x4 normal, int material, uint sourceInstance, int planeStride = 0)
    {
        var stride = planeStride == 0 ? count : planeStride;
        if (first < 0 || count < 0 || first > VertexCount - count || stride < count || sourceInstance > 1u << 24
            || packed.Length < checked(stride * 3 + count))
            throw new ArgumentOutOfRangeException(nameof(count));
        var vertices = _tree is not null ? _tree.Vertices : _raster is not null ? _raster.Product.Vertices : _page!.Value.Page.Vertices;
        var bakeUvs = _tree is not null ? _tree.BakeUvs : _raster is not null ? _raster.Product.BakeUvs : _page!.Value.Page.BakeUvs;
        for (var v = 0; v < count; v++) {
            var source = _source is null ? first + v : _source[first + v];
            var vertex = vertices[source];
            var position = math.mul(transform, new float4(vertex.Position, 1)).xyz;
            var n = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
            var t = math.mul(transform, new float4(vertex.Tangent.xyz, 0)).xyz;
            packed[v] = new(position, n.x);
            packed[stride + v] = new(n.y, n.z, vertex.UV.x, vertex.UV.y);
            packed[stride * 2 + v] = new(t, (vertex.Tangent.w < 0 ? -1 : 1) * (material + 1));
            var bakeUv = bakeUvs.IsEmpty ? default : bakeUvs[source];
            packed[stride * 3 + v] = new(bakeUv.x, bakeUv.y, bakeUvs.IsEmpty ? 0 : 1,
                sourceInstance);
        }
    }

    internal void PackRasterVertices(Span<float4> attributes,Span<uint4> tangentBake,int first,int count,int planeStride=0)
    {
        if(_raster is null) throw new InvalidOperationException("Compact Raster packing requires an acquired Raster section.");
        var stride=planeStride==0?count:planeStride;
        if(first<0 || count<0 || first>VertexCount-count || stride<count || attributes.Length<checked(stride+count) || tangentBake.Length<count)
            throw new ArgumentOutOfRangeException(nameof(count));
        var product=_raster.Product;
        for(var at=0;at<count;at++) {
            var index=_source![first+at]; var vertex=product.Vertices[index];
            attributes[at]=new(vertex.Position,vertex.Normal.x);
            attributes[stride+at]=new(vertex.Normal.y,vertex.Normal.z,vertex.UV.x,vertex.UV.y);
            tangentBake[at]=PbrRasterVertex.Pack(vertex.Tangent,product.HasBakeUvs?product.BakeUvs[index]:default,product.HasBakeUvs);
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
        if (_page is { } page) {
            foreach (var index in page.Page.Indices.Slice(page.FirstTriangle * 3, page.TriangleCount * 3)) writer.Append(checked(vertexOffset + (uint)_remap![index]));
            return;
        }
        if (_raster is not null) {
            foreach (var index in _raster.Indices) writer.Append(checked(vertexOffset + _rasterRemap![index]));
            return;
        }
        if (_remap is not null) {
            foreach (var cluster in _tree!.Meshlets) {
                var vertices = _tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount);
                foreach (var local in _tree.MeshletTriangleIndices.Slice(cluster.TriangleOffset, cluster.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + (uint)_remap[vertices[local]]));
            }
        }
        else {
            foreach (var node in _tree!.Nodes.Span) {
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
