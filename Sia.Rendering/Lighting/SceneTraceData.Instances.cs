using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class SceneTraceData
{
    public ReadOnlyMemory<uint4> Geometry { get; }
    public ReadOnlyMemory<uint4> Instances { get; }
    public bool IsInstanced => !Geometry.IsEmpty;
    public ulong PayloadBytes => ((ulong)Packed.Length + (ulong)Geometry.Length + (ulong)Instances.Length) * 16;

    /// <summary>
    /// Copies complete instance_trace packets and two surface rows per material:
    /// albedo/sidedness, emission/alpha-blend. Checks storage safety, not provenance or BVH quality.
    /// Packed contains surfaces for this input; Geometry and Instances are independent buffers.
    /// </summary>
    public SceneTraceData(ReadOnlySpan<uint4> geometry, ReadOnlySpan<uint4> instances,
        ReadOnlySpan<float4> surfaces, ulong maximumBytes = 128ul * 1024 * 1024,
        ReadOnlySpan<byte> sceneIdentity = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!sceneIdentity.IsEmpty && sceneIdentity.Length != 32) throw new ArgumentException("Scene identity must contain 32 bytes.", nameof(sceneIdentity));
        if (((ulong)geometry.Length + (ulong)instances.Length + (ulong)surfaces.Length) * 16 > maximumBytes)
            throw new ArgumentException("Instance transport exceeds its owned-buffer budget.", nameof(maximumBytes));
        Require(geometry.Length >= 3 && instances.Length >= 5 && surfaces.Length % 2 == 0);
        Require(geometry[0].w == 0x32475453 && instances[2].z == 0x31495453);
        Require(geometry[0].z == geometry.Length && instances[2].y == instances.Length);
        Require(geometry[1].Equals(instances[3]) && geometry[2].Equals(instances[4]));
        var meshes = geometry[0]; var top = instances[0]; var tables = instances[1];
        Range(meshes.y, (ulong)meshes.x * 2, geometry.Length);
        Range(top.w, (ulong)top.z * 5, instances.Length);
        Range(instances[2].x, ((ulong)top.z + 3) / 4, instances.Length);
        Range(tables.x, tables.y, instances.Length); Range(tables.z, tables.w, instances.Length);
        ValidateNodes(instances, top.y, top.x, top.z, cancellationToken);
        Require((top.x == 0) == (top.z == 0));
        var triangles = 0;
        for (uint mesh = 0; mesh < meshes.x; mesh++) {
            cancellationToken.ThrowIfCancellationRequested();
            var part = geometry[(int)(meshes.y + mesh * 2)]; var vertices = geometry[(int)(meshes.y + mesh * 2 + 1)];
            Require((ulong)part.z + (ulong)part.w * 5 <= (ulong)geometry.Length * 4); Range(vertices.x, vertices.y, geometry.Length);
            Range(part.x, (ulong)part.y * 2, geometry.Length);
            for (uint n = 0; n < part.y; n++) {
                if ((n & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var lo = geometry[(int)(part.x + n * 2)]; var hi = geometry[(int)(part.x + n * 2 + 1)];
                Require(math.all(math.isfinite(Vector(lo))) && math.all(math.isfinite(Vector(hi)))
                    && math.all(Vector(lo) <= Vector(hi)) && lo.w > n && lo.w <= part.y
                    && (ulong)(hi.w >> 7) + (hi.w & 127) <= part.w);
            }
            Require(part.w == 0 || part.y != 0); Require((ulong)triangles + part.w <= int.MaxValue); triangles += (int)part.w;
            for (uint t = 0; t < part.w; t++) {
                if ((t & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var at = part.z + t * 5;
                Require(GeometryWord(geometry, at) < vertices.y && GeometryWord(geometry, at + 1) < vertices.y && GeometryWord(geometry, at + 2) < vertices.y);
            }
            for (uint v = 0; v < vertices.y; v++) {
                if ((v & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                Require(math.all(math.isfinite(Vector(geometry[(int)(vertices.x + v)]))));
            }
        }
        for (uint i = 0; i < top.z; i++) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            Require(instances[(int)(instances[2].x + i / 4)][(int)(i % 4)] < top.z);
            var offset = (int)(top.w + i * 5); var meta = instances[offset];
            Require(meta.x < tables.y && meta.y < top.z && meta.z == i);
            for (var row = 2; row < 5; row++) {
                var value = instances[offset + row];
                Require(math.all(math.isfinite(Vector(value))) && float.IsFinite(BitConverter.UInt32BitsToSingle(value.w)));
            }
        }
        for (uint i = 0; i < tables.y; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = instances[(int)(tables.x + i)]; Require(binding.x < meshes.x);
            Require((ulong)binding.y + binding.z <= tables.w); uint previous = 0;
            for (uint s = 0; s < binding.z; s++) {
                var slot = instances[(int)(tables.z + binding.y + s)];
                Require(slot.y < surfaces.Length / 2 && (s == 0 || slot.x > previous)); previous = slot.x;
            }
        }
        for (var i = 0; i < surfaces.Length; i++) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            Require(math.all(math.isfinite(surfaces[i])) && math.all(surfaces[i].xyz >= 0)
                && (surfaces[i].w == 0 || surfaces[i].w == 1));
        }
        Geometry = geometry.ToArray(); Instances = instances.ToArray(); Packed = surfaces.ToArray();
        Bounds = top.x == 0 ? new(float3.zero, float3.zero)
            : new(Vector(instances[(int)top.y]), Vector(instances[(int)top.y + 1]));
        TriangleCount = triangles;
        if (!sceneIdentity.IsEmpty) Identity = sceneIdentity.ToArray();
        else {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("SIAINSTANCEPROBETRACE1"u8);
            hash.AppendData(MemoryMarshal.AsBytes(Geometry.Span)); hash.AppendData(MemoryMarshal.AsBytes(Instances.Span));
            hash.AppendData(MemoryMarshal.AsBytes(Packed.Span)); Identity = hash.GetHashAndReset();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateNodes(ReadOnlySpan<uint4> rows, uint offset, uint count, uint items, CancellationToken token)
    {
        Range(offset, (ulong)count * 3, rows.Length);
        for (uint i = 0; i < count; i++) {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var lo = rows[(int)(offset + i * 3)]; var hi = rows[(int)(offset + i * 3 + 1)];
            Require(math.all(math.isfinite(Vector(lo))) && math.all(math.isfinite(Vector(hi)))
                && math.all(Vector(lo) <= Vector(hi)) && lo.w > i && lo.w <= count);
            Require((ulong)hi.w + rows[(int)(offset + i * 3 + 2)].x <= items);
        }
    }
    private static float3 Vector(uint4 row) => new(BitConverter.UInt32BitsToSingle(row.x), BitConverter.UInt32BitsToSingle(row.y), BitConverter.UInt32BitsToSingle(row.z));
    private static uint GeometryWord(ReadOnlySpan<uint4> rows, uint word) => rows[(int)(word / 4)][(int)(word % 4)];
    private static void Range(uint offset, ulong count, int length) => Require((ulong)offset + count <= (ulong)length);
    private static void Require(bool valid) { if (!valid) throw new ArgumentException("Invalid instance trace buffer layout or value."); }
}
