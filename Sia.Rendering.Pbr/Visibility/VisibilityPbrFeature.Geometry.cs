using System.Buffers;
using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WorkGpu(uint Triangle, uint Instance);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TriangleGpu(uint VertexOffset, uint PackedCorners);

    private static Entity UploadVertices(World world, WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        ReadOnlySpan<MeshVertex> vertices, WGPULimits limits, List<Entity> acquired)
    {
        // One 48-byte-per-vertex buffer, with contiguous attribute planes so depth
        // draws fetch only the position plane. All source float bits are preserved.
        var length = System.Math.Max(1, vertices.Length);
        var entity = Allocate(world, device, (ulong)length * 48,
            WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst, limits, acquired);
        var staging = ArrayPool<float4>.Shared.Rent(System.Math.Min(16384, length));
        try {
            for (var plane = 0; plane < 3; plane++) {
                for (var offset = 0; offset < vertices.Length;) {
                    var count = System.Math.Min(staging.Length, vertices.Length - offset);
                    for (var i = 0; i < count; i++) {
                        var vertex = vertices[offset + i];
                        staging[i] = plane switch {
                            0 => new(vertex.Position, vertex.Normal.x),
                            1 => new(vertex.Normal.y, vertex.Normal.z, vertex.UV.x, vertex.UV.y),
                            _ => vertex.Tangent
                        };
                    }
                    Wgpu.WriteBuffer<float4>(queue, entity.GetWgpu<WGPUBuffer>(),
                        ((ulong)plane * (ulong)length + (ulong)offset) * 16, staging.AsSpan(0, count));
                    offset += count;
                }
            }
        }
        finally { ArrayPool<float4>.Shared.Return(staging); }
        return entity;
    }

    private static Entity UploadTriangles(World world, WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        MeshletRasterData geometry, WGPULimits limits, List<Entity> acquired)
    {
        var triangles = geometry.Triangles.Span;
        var length = System.Math.Max(1, triangles.Length);
        var entity = Allocate(world, device, (ulong)length * 8,
            WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst, limits, acquired);
        var staging = ArrayPool<TriangleGpu>.Shared.Rent(System.Math.Min(16384, length));
        try {
            for (var offset = 0; offset < triangles.Length;) {
                var count = System.Math.Min(staging.Length, triangles.Length - offset);
                for (var i = 0; i < count; i++) {
                    var triangle = triangles[offset + i];
                    var meshlet = geometry.Meshlets.Span[(int)triangle.x];
                    staging[i] = new(meshlet.x, geometry.Indices.Span[checked((int)(meshlet.y + triangle.y))]);
                }
                Wgpu.WriteBuffer<TriangleGpu>(queue, entity.GetWgpu<WGPUBuffer>(), (ulong)offset * 8, staging.AsSpan(0, count));
                offset += count;
            }
        }
        finally { ArrayPool<TriangleGpu>.Shared.Return(staging); }
        return entity;
    }
}
