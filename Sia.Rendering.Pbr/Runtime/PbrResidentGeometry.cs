using System.Buffers;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed class PbrResidentGeometry : IDisposable
{
    internal delegate void IndexUpload(ReadOnlySpan<uint> values, int first);

    private readonly PbrRasterGeometry? _raster;
    private readonly PbrSceneStream.PageGeometry? _page;
    private readonly Dictionary<uint, uint>? _rasterRemap;
    private int[]? _remap;
    private int[]? _source;

    public int VertexCount { get; }
    public uint TriangleCount { get; }
    public Aabb Bounds { get; }

    public PbrResidentGeometry(PbrRasterGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _raster = geometry; Bounds = geometry.Bounds; TriangleCount = (uint)(geometry.Indices.Length / 3);
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
        TriangleCount = (uint)geometry.TriangleCount;
        // A shared page may contain many unrelated node ranges. Transparent draws
        // own only their selected range, just as Raster draws own one section.
        try {
            _remap = ArrayPool<int>.Shared.Rent(geometry.Page.VertexCount);
            _remap.AsSpan(0, geometry.Page.VertexCount).Fill(-1);
            _source = ArrayPool<int>.Shared.Rent(System.Math.Min(geometry.Page.VertexCount, geometry.TriangleCount * 3));
            var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
            foreach (var index in geometry.Page.Indices.Slice(geometry.FirstTriangle * 3, geometry.TriangleCount * 3)) {
                if (_remap[index] >= 0) continue;
                _remap[index] = VertexCount; _source[VertexCount++] = (int)index;
                var position = geometry.Page.GetVertex((int)index).Position;
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
        var vertices = _raster is not null ? _raster.Product.Vertices : default;
        var bakeUvs = _raster is not null ? _raster.Product.BakeUvs : default;
        var hasBake = _page is { } pageGeometry ? pageGeometry.Page.HasBakeUvs : !bakeUvs.IsEmpty;
        for (var v = 0; v < count; v++) {
            var source = _source is null ? first + v : _source[first + v];
            var vertex = _page is { } page ? page.Page.GetVertex(source) : vertices[source];
            var position = math.mul(transform, new float4(vertex.Position, 1)).xyz;
            var n = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
            var t = math.mul(transform, new float4(vertex.Tangent.xyz, 0)).xyz;
            packed[v] = new(position, n.x);
            packed[stride + v] = new(n.y, n.z, vertex.UV.x, vertex.UV.y);
            packed[stride * 2 + v] = new(t, (vertex.Tangent.w < 0 ? -1 : 1) * (material + 1));
            var bakeUv = _page is { } bakedPage ? bakedPage.Page.GetBakeUv(source) : hasBake ? bakeUvs[source] : default;
            packed[stride * 3 + v] = new(bakeUv.x, bakeUv.y, hasBake ? 1 : 0,
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

    public void WriteIndices(ref IndexWriter writer, uint vertexOffset, int lod = 0)
    {
        if (_page is { } page) {
            foreach (var index in page.Page.Indices.Slice(page.FirstTriangle * 3, page.TriangleCount * 3)) writer.Append(checked(vertexOffset + (uint)_remap![index]));
            return;
        }
        if (_raster is not null) {
            foreach (var index in _raster.GetIndices(lod)) writer.Append(checked(vertexOffset + _rasterRemap![index]));
            return;
        }
        throw new InvalidOperationException("Resident geometry requires an acquired Raster section or page.");
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
