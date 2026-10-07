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
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                Coarse = checked(Coarse + (uint)node.TriangleCount);
                Error = MathF.Max(Error, node.EstimatedSpatialError);
            }
        }
        try {
            _remap = ArrayPool<int>.Shared.Rent(tree.VertexCount);
            _source = ArrayPool<int>.Shared.Rent(tree.VertexCount);
            _remap.AsSpan(0, tree.VertexCount).Fill(-1);
            if (tree.RootCount != tree.Nodes.Length) {
                // Conventional rendering uses only the finest and root cuts.
                // Intermediate-only vertices must not consume GPU geometry storage.
                foreach (var node in tree.Nodes.Span) {
                    if (node.Parent >= 0 && node.ChildCount != 0) continue;
                    foreach (var vertex in tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3)) {
                        if (_remap[vertex] >= 0) continue;
                        _remap[vertex] = VertexCount;
                        _source[VertexCount++] = (int)vertex;
                    }
                }
            }
            else {
                foreach (var cluster in tree.Meshlets) {
                    foreach (var vertex in tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount)) {
                        if (_remap[vertex] >= 0) continue;
                        _remap[vertex] = VertexCount;
                        _source[VertexCount++] = (int)vertex;
                    }
                }
            }
        }
        catch {
            Dispose();
            throw;
        }
    }

    public void PackVertices(Span<float4> packed, int first, int count, float4x4 transform,
        float4x4 normal, int material, int planeStride = 0, float4? lightmapScaleBias = null, int lightmapReceiver = 0,
        ReadOnlySpan<int> lightmapCharts = default, int chartResolution = 0)
    {
        var stride = planeStride == 0 ? count : planeStride;
        if (first < 0 || count < 0 || first > VertexCount - count || stride < count
            || packed.Length < checked(stride * (lightmapScaleBias.HasValue ? 3 : 2) + count))
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
            if (lightmapScaleBias is { } mapping) {
                var uv = vertex.LightmapUV * mapping.xy + mapping.zw;
                var marker = mapping.x > 0 && mapping.y > 0 ? lightmapReceiver + 1 : 0;
                if (!lightmapCharts.IsEmpty) {
                    marker = ChartAt(vertex.LightmapUV, lightmapCharts, chartResolution) + 1;
                }
                packed[stride * 3 + v] = new(uv.x, uv.y, marker, 0);
            }
        }
    }

    internal void ValidateLightmapCharts(ReadOnlySpan<int> charts, int resolution)
    {
        var indices = _tree.Indices;
        var vertices = _tree.Vertices;
        // Both finest and stored coarse triangles must retain a constant chart marker.
        for (var t = 0; t < indices.Length; t += 3) {
            var a = ChartAt(vertices[(int)indices[t]].LightmapUV, charts, resolution);
            var b = ChartAt(vertices[(int)indices[t + 1]].LightmapUV, charts, resolution);
            var c = ChartAt(vertices[(int)indices[t + 2]].LightmapUV, charts, resolution);
            if (a != b || a != c) throw new ArgumentException("A lightmap triangle crosses padded chart allocations.");
        }
    }

    public void PackCompactVertices(Span<uint> packed, int first, int count, float4x4 transform,
        float4x4 normal, int material, int planeStride, float4? lightmapScaleBias = null, int lightmapReceiver = 0,
        ReadOnlySpan<int> lightmapCharts = default, int chartResolution = 0, int destinationFirst = 0, int lightmapWordStride = 2,
        int lightmapOwnerBits = 0)
    {
        if (lightmapWordStride is not (1 or 2 or 4) || lightmapOwnerBits is < 0 or > 30
            || (lightmapWordStride == 1) != (lightmapOwnerBits > 0)
            || material < 0 || first < 0 || count < 0 || first > VertexCount - count || destinationFirst < 0
            || planeStride < destinationFirst + count || packed.Length < checked(lightmapScaleBias.HasValue
                ? planeStride * 8 + (destinationFirst + count) * lightmapWordStride : planeStride * 4 + (destinationFirst + count) * 4))
            throw new ArgumentOutOfRangeException(nameof(count));
        for (var v = 0; v < count; v++) {
            var vertex = _tree.Vertices[_source![first + v]];
            var p = math.mul(transform, new float4(vertex.Position, 1)).xyz;
            var n = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
            var t = math.mul(transform, new float4(vertex.Tangent.xyz, 0)).xyz;
            var a = (destinationFirst + v) * 4;
            packed[a] = BitConverter.SingleToUInt32Bits(p.x);
            packed[a + 1] = BitConverter.SingleToUInt32Bits(p.y);
            packed[a + 2] = BitConverter.SingleToUInt32Bits(p.z);
            packed[a + 3] = PackDirection(n);
            var b = planeStride * 4 + a;
            packed[b] = BitConverter.SingleToUInt32Bits(vertex.UV.x);
            packed[b + 1] = BitConverter.SingleToUInt32Bits(vertex.UV.y);
            packed[b + 2] = PackDirection(t);
            packed[b + 3] = (uint)material | (vertex.Tangent.w < 0 ? 0x80000000u : 0);
            if (lightmapScaleBias is { } mapping) {
                var uv = vertex.LightmapUV * mapping.xy + mapping.zw;
                var marker = mapping.x > 0 && mapping.y > 0 ? lightmapReceiver + 1 : 0;
                if (!lightmapCharts.IsEmpty) marker = ChartAt(vertex.LightmapUV, lightmapCharts, chartResolution) + 1;
                if (!math.all(math.isfinite(uv)) || math.any(uv < 0) || math.any(uv > 1))
                    throw new ArgumentException("Packed lightmap coordinates must be within the atlas.");
                var c = planeStride * 8 + (destinationFirst + v) * lightmapWordStride;
                packed[c] = (uint)MathF.Round(uv.x * 65535) | (uint)MathF.Round(uv.y * 65535) << 16;
                if (lightmapOwnerBits > 0)
                    packed[b + 3] = PackLightmapOwner((uint)material, (uint)marker, vertex.Tangent.w < 0, lightmapOwnerBits);
                else packed[c + 1] = (uint)marker;
                if (lightmapWordStride == 4) { packed[c + 2] = 0; packed[c + 3] = 0; }
            }
        }
    }

    // Bit31 remains tangent handedness. Zero selects the original separate marker word.
    internal static int PackedLightmapOwnerBits(int ownerCount, int markerCount)
    {
        if (ownerCount < 1 || markerCount < 1) return 0;
        var owners = System.Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(ownerCount - 1)));
        var markers = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)markerCount);
        return owners + markers <= 31 ? owners : 0;
    }

    internal static uint PackLightmapOwner(uint owner, uint marker, bool negative, int ownerBits)
    {
        if (ownerBits is < 1 or > 30 || owner >= (1u << ownerBits) || marker > (0x7fffffffu >> ownerBits))
            throw new ArgumentOutOfRangeException(nameof(ownerBits), "Owner and lightmap marker must fit below tangent handedness.");
        return owner | marker << ownerBits | (negative ? 0x80000000u : 0);
    }

    // Octahedral SNORM16x2; the unused -32768/-32768 pair preserves a zero vector.
    internal static uint PackDirection(float3 direction)
    {
        if (!math.all(math.isfinite(direction))) throw new ArgumentException("Non-finite vertex direction.");
        var magnitude = MathF.Max(MathF.Abs(direction.x), MathF.Max(MathF.Abs(direction.y), MathF.Abs(direction.z)));
        if (magnitude == 0) return 0x80008000u;
        var n = direction / magnitude;
        n /= MathF.Abs(n.x) + MathF.Abs(n.y) + MathF.Abs(n.z);
        var xy = n.z >= 0 ? n.xy : new float2((1 - MathF.Abs(n.y)) * (n.x >= 0 ? 1 : -1),
            (1 - MathF.Abs(n.x)) * (n.y >= 0 ? 1 : -1));
        return (ushort)(short)MathF.Round(xy.x * 32767) | (uint)(ushort)(short)MathF.Round(xy.y * 32767) << 16;
    }

    private static int ChartAt(float2 uv, ReadOnlySpan<int> charts, int resolution)
    {
        if (!math.all(math.isfinite(uv)) || math.any(uv < 0) || math.any(uv > 1))
            throw new ArgumentException("Lightmap vertex is outside its receiver coordinates.");
        var x = System.Math.Min((int)MathF.Floor(uv.x * resolution), resolution - 1);
        var y = System.Math.Min((int)MathF.Floor(uv.y * resolution), resolution - 1);
        var chart = charts[y * resolution + x];
        if (chart < 0) throw new ArgumentException("Lightmap vertex is outside its receiver's padded charts.");
        return chart;
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
        if (_tree.RootCount == _tree.Nodes.Length) {
            foreach (var cluster in _tree.Meshlets) {
                var vertices = _tree.MeshletVertexIndices.Slice(cluster.VertexOffset, cluster.VertexCount);
                foreach (var local in _tree.MeshletTriangleIndices.Slice(cluster.TriangleOffset, cluster.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + (uint)_remap![vertices[local]]));
            }
        }
        else {
            foreach (var node in _tree.Nodes.Span) {
                if (node.ChildCount != 0) continue;
                foreach (var index in _tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + (uint)_remap![index]));
            }
            foreach (var node in _tree.Nodes.Span[.._tree.RootCount]) {
                foreach (var index in _tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3))
                    writer.Append(checked(vertexOffset + (uint)_remap![index]));
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
