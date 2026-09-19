using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

// Cooked buffer data; no mesh hierarchy is decoded when a detail page arrives.
public sealed class GeometryPage
{
    public readonly record struct Counts(int Vertices, int Indices, int Triangles, int Clusters)
    {
        public long ByteLength => 24L + Vertices * 48L + Indices * 4L + Triangles * 8L + Clusters * 80L;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Cluster(float X, float Y, float Z, uint VertexOffset,
        float4 Maximum, float4 Sphere, float4 Cone, uint4 Work);
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Triangle(uint x, uint y);

    public ReadOnlyMemory<byte> Bytes { get; }
    public Counts Size { get; }
    public ReadOnlySpan<float4> Vertices => MemoryMarshal.Cast<byte, float4>(Bytes.Span.Slice(24, Size.Vertices * 48));
    public ReadOnlySpan<uint> Indices => MemoryMarshal.Cast<byte, uint>(Bytes.Span.Slice(24 + Size.Vertices * 48, Size.Indices * 4));
    public ReadOnlySpan<Triangle> Triangles => MemoryMarshal.Cast<byte, Triangle>(Bytes.Span.Slice(24 + Size.Vertices * 48 + Size.Indices * 4, Size.Triangles * 8));
    public ReadOnlySpan<Cluster> Clusters => MemoryMarshal.Cast<byte, Cluster>(Bytes.Span[^ (Size.Clusters * 80)..]);

    public Aabb Bounds
    {
        get {
            var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
            foreach (var cluster in Clusters) {
                minimum = math.min(minimum, new float3(cluster.X, cluster.Y, cluster.Z));
                maximum = math.max(maximum, cluster.Maximum.xyz);
            }
            return new(minimum, maximum);
        }
    }

    private GeometryPage(ReadOnlyMemory<byte> bytes, Counts size) { Bytes = bytes; Size = size; }

    public static GeometryPage Decode(ReadOnlyMemory<byte> bytes)
    {
        if (!BitConverter.IsLittleEndian || bytes.Length < 24 || !bytes.Span[..8].SequenceEqual("SIAGEO01"u8))
            throw new InvalidDataException("Invalid geometry page header.");
        var size = new Counts(BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[12..]), BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[20..]));
        if (size.Vertices <= 0 || size.Indices <= 0 || size.Triangles <= 0 || size.Clusters <= 0
            || size.ByteLength != bytes.Length || bytes.Length > Sia.Asset.AssetChunk.MaximumLength)
            throw new InvalidDataException("Invalid geometry page lengths.");
        var page = new GeometryPage(bytes, size);
        var indices = page.Indices; var triangles = page.Triangles;
        foreach (var v in page.Vertices) if (!Finite(v)) throw new InvalidDataException("Nonfinite geometry.");
        int nextTriangle = 0, nextReference = 0;
        var referenceCount = size.Indices - size.Triangles;
        foreach (var c in page.Clusters) {
            if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !Finite(c.Maximum) || !Finite(c.Sphere) || !Finite(c.Cone)
                || c.X > c.Maximum.x || c.Y > c.Maximum.y || c.Z > c.Maximum.z || c.Sphere.w < 0
                || c.Work.x != nextTriangle || c.VertexOffset != nextReference || c.Work.y == 0 || c.Work.y > 256
                || c.Work.z == 0 || c.Work.z > 124 || c.Work.w != referenceCount + nextTriangle
                || (long)nextReference + c.Work.y > referenceCount || (long)nextTriangle + c.Work.z > size.Triangles)
                throw new InvalidDataException("Invalid geometry cluster.");
            for (var i = 0; i < c.Work.y; i++) if (indices[nextReference + i] >= size.Vertices) throw new InvalidDataException("Invalid vertex reference.");
            for (var i = 0; i < c.Work.z; i++) {
                var t = triangles[nextTriangle + i];
                if (t.x != c.VertexOffset || t.y != indices[referenceCount + nextTriangle + i]
                    || (t.y & 255) >= c.Work.y || ((t.y >> 8) & 255) >= c.Work.y || (t.y >> 16) >= c.Work.y)
                    throw new InvalidDataException("Invalid triangle reference.");
            }
            nextTriangle += (int)c.Work.z; nextReference += (int)c.Work.y;
        }
        if (nextTriangle != size.Triangles || nextReference != referenceCount) throw new InvalidDataException("Incomplete geometry page.");
        return page;
    }

    public static GeometryPage Cook(MeshPatchAsset asset)
    {
        var (mesh, meshlets) = asset.Build.Tree.CopyFinestGeometry();
        var raster = MeshletRasterData.Create(mesh, meshlets);
        var unique = new List<MeshVertex>(); var identities = new Dictionary<MeshVertex, uint>();
        var remap = new uint[raster.Vertices.Length];
        for (var i = 0; i < remap.Length; i++) {
            var vertex = raster.Vertices.Span[i];
            if (!identities.TryGetValue(vertex, out var mapped)) { mapped = (uint)unique.Count; identities.Add(vertex, mapped); unique.Add(vertex); }
            remap[i] = mapped;
        }
        var size = new Counts(unique.Count, raster.Indices.Length, raster.Triangles.Length, meshlets.Meshlets.Length);
        if (size.ByteLength > Sia.Asset.AssetChunk.MaximumLength) throw new ArgumentException("Split geometry larger than one chunk before cooking.");
        var bytes = new byte[checked((int)size.ByteLength)];
        "SIAGEO01"u8.CopyTo(bytes);
        var header = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(8, 16));
        header[0] = size.Vertices; header[1] = size.Indices; header[2] = size.Triangles; header[3] = size.Clusters;
        var vertices = MemoryMarshal.Cast<byte, float4>(bytes.AsSpan(24, size.Vertices * 48));
        for (var i = 0; i < size.Vertices; i++) {
            var v = unique[i];
            vertices[i] = new(v.Position, v.Normal.x);
            vertices[size.Vertices + i] = new(v.Normal.y, v.Normal.z, v.UV.x, v.UV.y);
            vertices[size.Vertices * 2 + i] = v.Tangent;
        }
        var indices = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(24 + size.Vertices * 48, size.Indices * 4));
        for (var i = 0; i < size.Indices; i++) indices[i] = i < size.Indices - size.Triangles ? remap[raster.Indices.Span[i]] : raster.Indices.Span[i];
        var triangles = MemoryMarshal.Cast<byte, Triangle>(bytes.AsSpan(24 + size.Vertices * 48 + size.Indices * 4, size.Triangles * 8));
        var clusters = MemoryMarshal.Cast<byte, Cluster>(bytes.AsSpan(bytes.Length - size.Clusters * 80));
        for (var i = 0; i < clusters.Length; i++) {
            var c = meshlets.Meshlets[i]; var descriptor = raster.Meshlets.Span[i];
            clusters[i] = new(c.Bounds.Box.Min.x, c.Bounds.Box.Min.y, c.Bounds.Box.Min.z, descriptor.x,
                new(c.Bounds.Box.Max, 0), new(c.Bounds.Center, c.Bounds.Radius), new(c.Bounds.ConeAxis, c.Bounds.ConeCutoff),
                new((uint)c.TriangleOffset / 3, (uint)c.VertexCount, (uint)c.TriangleCount, descriptor.y));
            for (var t = 0; t < c.TriangleCount; t++) triangles[c.TriangleOffset / 3 + t] = new(descriptor.x, raster.Indices.Span[(int)descriptor.y + t]);
        }
        return Decode(bytes);
    }

    private static bool Finite(float4 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z) && float.IsFinite(v.w);
}
