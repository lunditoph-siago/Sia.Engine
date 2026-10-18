using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrBvhTrace
{
    // Runtime uint4 layouts, not serialized asset formats. See rendering/Geometry/instance_trace.
    private const uint GeometryMagic = 0x32475453; // STG2
    private const uint InstanceMagic = 0x31495453; // STI1

    /// <summary>Owned GPU rows; one local BVH per scene geometry entry, independent of transforms/material overrides.</summary>
    public uint4[] PackGpuGeometry(ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong rowCount = 3 + (ulong)Scene.Geometry.Length * 2;
        var positionCounts = new int[Scene.Geometry.Length];
        for (var i = 0; i < Scene.Geometry.Length; i++) {
            var unique = new HashSet<uint3>(); var mesh = Scene.Geometry[i]; var at = 0;
            foreach (var position in mesh.Positions) {
                if ((at++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                unique.Add(PositionKey(position));
            }
            positionCounts[i] = unique.Count;
            rowCount += (ulong)mesh.Nodes.Length * 2 + ((ulong)mesh.Triangles.Length * 5 + 3) / 4 + (ulong)unique.Count;
        }
        var rows = AllocateRows(rowCount, maximumBytes);
        rows[0] = new((uint)Scene.Geometry.Length, 3, (uint)rows.Length, GeometryMagic);
        WriteGeometryDigest(rows.AsSpan(1, 2)); var offset = 3 + Scene.Geometry.Length * 2; var copied = 0;
        for (var i = 0; i < Scene.Geometry.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = Scene.Geometry[i]; var nodes = offset; var triangles = nodes + mesh.Nodes.Length * 2;
            var vertices = checked(triangles + (int)(((long)mesh.Triangles.Length * 5 + 3) / 4));
            rows[3 + i * 2] = new((uint)nodes, (uint)mesh.Nodes.Length, checked((uint)triangles * 4), (uint)mesh.Triangles.Length);
            rows[4 + i * 2] = new((uint)vertices, (uint)positionCounts[i], 0, 0);
            foreach (var node in mesh.Nodes) {
                if ((copied++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((uint)node.FirstTriangle > 0x1ffffff || (uint)node.TriangleCount > 127)
                    throw new ArgumentException("Local BVH node exceeds compact GPU addressing.");
                rows[offset++] = Bits(node.Bounds.Min, (uint)node.EscapeIndex);
                rows[offset++] = Bits(node.Bounds.Max, ((uint)node.FirstTriangle << 7) | (uint)node.TriangleCount);
            }
            var positions = new Dictionary<uint3, uint>(); var remap = new uint[mesh.Positions.Length];
            for (var vertex = 0; vertex < remap.Length; vertex++) {
                if ((copied++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var position = mesh.Positions[vertex]; var key = PositionKey(position);
                if (!positions.TryGetValue(key, out var index)) {
                    index = (uint)positions.Count; positions.Add(key, index); rows[vertices + (int)index] = Bits(position, 0);
                }
                remap[vertex] = index;
            }
            var word = checked(triangles * 4);
            foreach (var triangle in mesh.Triangles) {
                if ((copied++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                WriteWord(rows, word++, remap[triangle.A]); WriteWord(rows, word++, remap[triangle.B]); WriteWord(rows, word++, remap[triangle.C]);
                WriteWord(rows, word++, (uint)triangle.SourceTriangle); WriteWord(rows, word++, (uint)triangle.MaterialSlot);
            }
            offset = vertices + positionCounts[i];
        }
        cancellationToken.ThrowIfCancellationRequested(); return rows;
    }

    /// <summary>
    /// Owned GPU instance rows. Reuse geometry rows only when their digest matches rows 3/4 of this output.
    /// Float inverse admission is separate from the double CPU query; no GPU allocation or upload occurs.
    /// </summary>
    public uint4[] PackGpuInstances(ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slotCount = 0; foreach (var binding in Scene.Bindings) slotCount += binding.Materials.Length;
        var orderOffset = 5 + _nodes.Length * 3;
        var instanceOffset = orderOffset + (_order.Length + 3) / 4;
        var bindingOffset = instanceOffset + _instances.Length * 5;
        var slotOffset = bindingOffset + Scene.Bindings.Length;
        var rows = AllocateRows((ulong)slotOffset + (ulong)slotCount, maximumBytes);
        rows[0] = new((uint)_nodes.Length, 5, (uint)_instances.Length, (uint)instanceOffset);
        rows[1] = new((uint)bindingOffset, (uint)Scene.Bindings.Length, (uint)slotOffset, (uint)slotCount);
        rows[2] = new((uint)orderOffset, (uint)rows.Length, InstanceMagic, 0);
        WriteGeometryDigest(rows.AsSpan(3, 2));
        var offset = 5;
        foreach (var node in _nodes) {
            cancellationToken.ThrowIfCancellationRequested();
            rows[offset++] = Bits(node.Bounds.Min, (uint)node.Escape);
            rows[offset++] = Bits(node.Bounds.Max, (uint)node.First);
            rows[offset++] = new((uint)node.Count, 0, 0, 0);
        }
        for (var i = 0; i < _order.Length; i++) rows[orderOffset + i / 4][i % 4] = (uint)_order[i];
        var ranks = Enumerable.Range(0, _instances.Length).ToArray();
        Array.Sort(ranks, (a, b) => Scene.Instances[a].Id.CompareTo(Scene.Instances[b].Id));
        var rankByInstance = new uint[ranks.Length]; for (var i = 0; i < ranks.Length; i++) rankByInstance[ranks[i]] = (uint)i;
        Span<byte> id = stackalloc byte[16];
        for (var i = 0; i < _instances.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = Scene.Instances[i]; var transform = _instances[i]; offset = instanceOffset + i * 5;
            rows[offset] = new((uint)instance.Binding, rankByInstance[i], (uint)i, 0);
            instance.Id.TryWriteBytes(id, bigEndian: true, out _); rows[offset + 1] = Words(id);
            rows[offset + 2] = InverseRow(transform.Row0, transform.Translation.x);
            rows[offset + 3] = InverseRow(transform.Row1, transform.Translation.y);
            rows[offset + 4] = InverseRow(transform.Row2, transform.Translation.z);
        }
        var slot = 0;
        for (var i = 0; i < Scene.Bindings.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested(); var binding = Scene.Bindings[i];
            rows[bindingOffset + i] = new((uint)binding.Geometry, (uint)slot, (uint)binding.Materials.Length, 0);
            foreach (var material in binding.Materials) rows[slotOffset + slot++] = new((uint)material.Slot, (uint)material.Material, 0, 0);
        }
        cancellationToken.ThrowIfCancellationRequested(); return rows;
    }

    private static uint4[] AllocateRows(ulong count, ulong maximumBytes)
    {
        if (count > int.MaxValue || count * 16 > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Packed trace rows exceed the encoded buffer budget.");
        return new uint4[(int)count];
    }
    private void WriteGeometryDigest(Span<uint4> output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIABVHGPUTABLE2"u8);
        foreach (var mesh in Scene.Geometry) hash.AppendData(Convert.FromHexString(mesh.BuildKey));
        Span<byte> digest = stackalloc byte[32]; hash.GetHashAndReset(digest);
        output[0] = Words(digest[..16]); output[1] = Words(digest[16..]);
    }
    private static uint4 Words(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
    private static uint4 Bits(float3 value, uint w) => new(BitConverter.SingleToUInt32Bits(value.x), BitConverter.SingleToUInt32Bits(value.y), BitConverter.SingleToUInt32Bits(value.z), w);
    private static uint3 PositionKey(float3 value) => new(BitConverter.SingleToUInt32Bits(value.x), BitConverter.SingleToUInt32Bits(value.y), BitConverter.SingleToUInt32Bits(value.z));
    private static void WriteWord(uint4[] rows, int word, uint value) => rows[word / 4][word % 4] = value;
    private static uint4 InverseRow(double3 row, double translation)
    {
        var value = new float4((float)row.x, (float)row.y, (float)row.z, (float)translation);
        if (!math.all(math.isfinite(value))) throw new ArgumentException("Instance inverse cannot be represented in GPU float precision.");
        return Bits(value.xyz, BitConverter.SingleToUInt32Bits(value.w));
    }
}
