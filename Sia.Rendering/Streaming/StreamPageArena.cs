using Sia;
using Sia.Math;
using Sia.WebGPU;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sia.Engine.Rendering;

public readonly record struct StreamPageAllocation(StreamRange Vertices, StreamRange Triangles);

public sealed class StreamPageArena : IDisposable
{
    public sealed class Upload
    {
        private readonly byte[] _storage;
        internal int Plane, Offset;
        internal bool Released;

        public StreamPageAllocation Allocation { get; }

        public bool Complete => Plane == 4;

        public long StagingBytes => ((long)Allocation.Vertices.Count * 48) + ((long)Allocation.Triangles.Count * 12);

        internal ReadOnlySpan<float4> Vertices
            => MemoryMarshal.Cast<byte, float4>(_storage.AsSpan(0, (int)Allocation.Vertices.Count * 48));

        internal ReadOnlySpan<uint> Indices
            => MemoryMarshal.Cast<byte, uint>(_storage.AsSpan((int)Allocation.Vertices.Count * 48, (int)Allocation.Triangles.Count * 12));

        internal Upload(StreamPageAllocation allocation, byte[] storage)
        {
            (Allocation, _storage) = (allocation, storage);
        }
    }

    private readonly GpuResources _gpu;
    private readonly StreamRangePool _vertices, _triangles;
    private readonly byte[] _staging = new byte[StreamGeometryPage.MaximumBytes - 16];
    private Upload? _current;

    public Entity Vertices { get; }
    public Entity Topology { get; }

    public long StagingBytes => _staging.LongLength;

    public uint VertexCapacity => _vertices.Capacity;

    public uint TriangleCapacity => _triangles.Capacity;

    public ulong Bytes => _gpu.Bytes;

    public ulong UsedBytes => ((ulong)_vertices.Used * 48) + ((ulong)_triangles.Used * 12);

    public StreamPageArena(in GpuFrame frame, uint vertexCapacity, uint triangleCapacity, ulong budget)
    {
        _gpu = new(frame, budget);
        _vertices = new(vertexCapacity);
        _triangles = new(triangleCapacity);
        try {
            Vertices = _gpu.Buffer((ulong)vertexCapacity * 48, WGPUBufferUsage.Storage | WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc);
            Topology = _gpu.Buffer((ulong)triangleCapacity * 12, WGPUBufferUsage.Storage | WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc);
        }
        catch { _gpu.Dispose(); throw; }
    }

    public bool TryReserve(uint vertices, uint triangles, out StreamPageAllocation allocation)
    {
        ArgumentOutOfRangeException.ThrowIfZero(vertices);
        ArgumentOutOfRangeException.ThrowIfZero(triangles);
        allocation = default;
        if (!_vertices.TryAllocate(vertices, out var v)) return false;
        if (!_triangles.TryAllocate(triangles, out var t)) {
            _vertices.Free(v);
            return false;
        }
        allocation = new(v, t);
        return true;
    }

    public Upload Prepare(StreamGeometryPage page, StreamPageAllocation allocation, float4x4 transform, float tangentMagnitude = 1)
    {
        Validate(allocation);
        if (_current is { Complete: false, Released: false })
            throw new InvalidOperationException("Complete or abort the current page upload first.");
        if (page.VertexCount != allocation.Vertices.Count || page.TriangleCount != allocation.Triangles.Count
            || !float.IsFinite(tangentMagnitude) || tangentMagnitude <= 0)
            throw new ArgumentException("Invalid geometry upload allocation.");
        var normal = math.transpose(math.inverse(transform));
        var packed = MemoryMarshal.Cast<byte, float4>(_staging.AsSpan(0, page.VertexCount * 48));
        var source = page.Vertices;
        for (var i = 0; i < page.VertexCount; i++) {
            var p = source[i];
            var n = source[page.VertexCount + i];
            var t = source[(page.VertexCount * 2) + i];
            var position = math.mul(transform, new float4(p.xyz, 1)).xyz;
            var direction = math.mul(normal, new float4(p.w, n.x, n.y, 0)).xyz;
            packed[i] = new(position, direction.x);
            packed[page.VertexCount + i] = new(direction.y, direction.z, n.z, n.w);
            packed[(page.VertexCount * 2) + i] = new(math.mul(transform, new float4(t.xyz, 0)).xyz, (t.w < 0 ? -1 : 1) * tangentMagnitude);
        }
        var indices = MemoryMarshal.Cast<byte, uint>(_staging.AsSpan(page.VertexCount * 48, page.TriangleCount * 12));
        var sourceIndices = page.Indices;
        RelocateIndices(sourceIndices, indices, allocation.Vertices.Offset);
        return _current = new(allocation, _staging);
    }

    internal static void RelocateIndices(ReadOnlySpan<uint> source, Span<uint> destination, uint vertexOffset)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Index destination is too short.", nameof(destination));

        var index = 0;
        if (Vector128.IsHardwareAccelerated) {
            var offset = Vector128.Create(vertexOffset);
            ref var sourceStart = ref MemoryMarshal.GetReference(source);
            ref var destinationStart = ref MemoryMarshal.GetReference(destination);
            for (; index <= source.Length - Vector128<uint>.Count; index += Vector128<uint>.Count) {
                var values = Vector128.LoadUnsafe(ref sourceStart, (nuint)index);
                (values + offset).StoreUnsafe(ref destinationStart, (nuint)index);
            }
        }
        for (; index < source.Length; index++)
            destination[index] = source[index] + vertexOffset;
    }

    public uint Advance(Upload upload, uint maximumBytes)
    {
        Validate(upload.Allocation);
        if (upload.Released) throw new InvalidOperationException("Upload has been released.");
        uint written = 0;
        while (!upload.Complete) {
            var vertex = upload.Plane < 3;
            var stride = vertex ? 16u : 4u;
            var length = vertex ? (int)upload.Allocation.Vertices.Count : upload.Indices.Length;
            var count = (int)System.Math.Min((maximumBytes - written) / stride, (uint)(length - upload.Offset));
            if (count == 0) break;
            if (vertex) {
                var offset = (((ulong)upload.Plane * VertexCapacity) + upload.Allocation.Vertices.Offset + (uint)upload.Offset) * 16;
                Wgpu.WriteBuffer<float4>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), offset,
                    upload.Vertices.Slice((upload.Plane * length) + upload.Offset, count));
            }
            else Wgpu.WriteBuffer<uint>(_gpu.Queue, Topology.GetWgpu<WGPUBuffer>(),
                (((ulong)upload.Allocation.Triangles.Offset * 3) + (uint)upload.Offset) * 4, upload.Indices.Slice(upload.Offset, count));
            upload.Offset += count;
            written += (uint)count * stride;
            if (upload.Offset == length) {
                upload.Plane++;
                upload.Offset = 0;
            }
        }
        return written;
    }

    public void Abort(Upload upload)
    {
        Validate(upload.Allocation);
        if (upload.Released) throw new InvalidOperationException("Upload has been released.");
        upload.Released = true;
        if (ReferenceEquals(upload, _current)) _current = null;
        Free(upload.Allocation);
    }

    public void Free(StreamPageAllocation allocation)
    {
        Validate(allocation);
        _vertices.Free(allocation.Vertices);
        _triangles.Free(allocation.Triangles);
    }

    private void Validate(StreamPageAllocation allocation)
    {
        if (!_vertices.Contains(allocation.Vertices) || !_triangles.Contains(allocation.Triangles))
            throw new InvalidOperationException("Stale or foreign geometry allocation.");
    }

    public void Dispose() => _gpu.Dispose();
}
