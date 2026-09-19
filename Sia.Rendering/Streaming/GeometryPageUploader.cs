using System.Buffers;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public readonly record struct GeometryPageAllocation(GeometryRangeAllocator.Range Vertices,
    GeometryRangeAllocator.Range Indices, GeometryRangeAllocator.Range Triangles)
{
    public long Bytes => Vertices.Length * 48L + Indices.Length * 4L + Triangles.Length * 8L;
}

/// <summary>Uploads one immutable geometry page within a byte budget on its owning queue.</summary>
public sealed class GeometryPageUploader
{
    private readonly GpuFrame _frame;
    private readonly Entity _vertices, _indices, _triangles;
    private readonly int _vertexCapacity;

    public GeometryPageUploader(in GpuFrame frame, Entity vertices, Entity indices, Entity triangles, int vertexCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexCapacity);
        _frame = frame; _vertices = vertices; _indices = indices; _triangles = triangles; _vertexCapacity = vertexCapacity;
    }

    public int Upload(GeometryPage page, GeometryPageAllocation allocation, ref int cursor, int budget)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ValidateAllocation(page, allocation);
        if ((long)allocation.Vertices.Offset + page.Size.Vertices > _vertexCapacity)
            throw new ArgumentOutOfRangeException(nameof(allocation));
        var queue = _frame.Queue.GetWgpu<WGPUQueue>(); var capacity = _vertexCapacity;
        var verticesEnd = page.Size.Vertices * 48; var indicesEnd = verticesEnd + page.Size.Indices * 4;
        var end = indicesEnd + page.Size.Triangles * 8; var started = cursor;
        var alignment = cursor < verticesEnd ? 16 : cursor < indicesEnd ? 4 : 8;
        var sectionStart = cursor < verticesEnd ? 0 : cursor < indicesEnd ? verticesEnd : indicesEnd;
        if (cursor < 0 || cursor > end || (cursor - sectionStart) % alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(cursor));
        if (cursor == end || budget < 16) return 0;
        // Slice/cast once per upload call, rather than per element on the WASM path.
        var indices = page.Indices; var triangles = page.Triangles;
        var referenceCount = indices.Length - triangles.Length;
        var staging = ArrayPool<uint>.Shared.Rent(16384);
        try { while (cursor < end && budget >= 16) {
            if (cursor < verticesEnd) {
                var planeBytes = page.Size.Vertices * 16; var plane = cursor / planeBytes; var offset = cursor % planeBytes;
                var count = System.Math.Min(planeBytes - offset, budget / 16 * 16);
                Wgpu.WriteBuffer<byte>(queue, _vertices.GetWgpu<WGPUBuffer>(), (ulong)(plane * (long)capacity + allocation.Vertices.Offset) * 16 + (ulong)offset,
                    page.Bytes.Span.Slice(24 + cursor, count));
                cursor += count; budget -= count;
            } else if (cursor < indicesEnd) {
                var offset = (cursor - verticesEnd) / 4; var count = System.Math.Min(System.Math.Min(page.Size.Indices - offset, budget / 4), 16384);
                for (var j = 0; j < count; j++) staging[j] = indices[offset + j] + (offset + j < referenceCount ? (uint)allocation.Vertices.Offset : 0);
                Wgpu.WriteBuffer<uint>(queue, _indices.GetWgpu<WGPUBuffer>(), (ulong)(allocation.Indices.Offset + offset) * 4, staging.AsSpan(0, count));
                cursor += count * 4; budget -= count * 4;
            } else {
                var offset = (cursor - indicesEnd) / 8; var count = System.Math.Min(System.Math.Min(page.Size.Triangles - offset, budget / 8), 8192);
                for (var j = 0; j < count; j++) { var triangle = triangles[offset + j]; staging[j * 2] = triangle.x + (uint)allocation.Indices.Offset; staging[j * 2 + 1] = triangle.y; }
                Wgpu.WriteBuffer<uint>(queue, _triangles.GetWgpu<WGPUBuffer>(), (ulong)(allocation.Triangles.Offset + offset) * 8, staging.AsSpan(0, count * 2));
                cursor += count * 8; budget -= count * 8;
            }
        }} finally { ArrayPool<uint>.Shared.Return(staging); }
        return cursor - started;
    }

    public static GeometryClusterGpu[] RelocateClusters(GeometryPage page, GeometryPageAllocation allocation, int capacity)
    {
        ArgumentNullException.ThrowIfNull(page);
        ValidateAllocation(page, allocation);
        if (capacity < page.Size.Clusters) throw new ArgumentOutOfRangeException(nameof(capacity));
        var clusters = new GeometryClusterGpu[capacity];
        var source = page.Clusters;
        for (var i = 0; i < page.Size.Clusters; i++) {
            var c = source[i];
            clusters[i] = new(c.X, c.Y, c.Z, c.VertexOffset + (uint)allocation.Indices.Offset, c.Maximum, c.Sphere, c.Cone,
                new(c.Work.x + (uint)allocation.Triangles.Offset, 0, c.Work.z, c.Work.w + (uint)allocation.Indices.Offset));
        }
        return clusters;
    }

    private static void ValidateAllocation(GeometryPage page, GeometryPageAllocation allocation)
    {
        if (allocation.Vertices.Offset < 0 || allocation.Indices.Offset < 0 || allocation.Triangles.Offset < 0
            || allocation.Vertices.Length < page.Size.Vertices || allocation.Indices.Length < page.Size.Indices
            || allocation.Triangles.Length < page.Size.Triangles
            || (long)allocation.Indices.Offset + allocation.Indices.Length > int.MaxValue
            || (long)allocation.Triangles.Offset + allocation.Triangles.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(allocation));
    }
}
