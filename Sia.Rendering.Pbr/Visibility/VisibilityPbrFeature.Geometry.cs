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
    private readonly record struct TriangleGpu(uint Meshlet, uint Triangle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PackedVertexGpu(float4 PositionNormalX, float4 NormalYZUv, float4 Tangent)
    {
        public static PackedVertexGpu From(MeshVertex vertex) => new(
            new(vertex.Position, vertex.Normal.x),
            new(vertex.Normal.y, vertex.Normal.z, vertex.UV.x, vertex.UV.y), vertex.Tangent);
    }

    private static unsafe Entity UploadPacked<TSource, T>(World world,
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue, ReadOnlySpan<TSource> source,
        Func<TSource, T> pack, WGPULimits limits, List<Entity> acquired) where T : unmanaged
    {
        var size = checked((ulong)System.Math.Max(1, source.Length) * (ulong)sizeof(T));
        if (size > limits.MaxBufferSize || size > limits.MaxStorageBufferBindingSize) {
            throw new ArgumentException("The resident geometry buffer exceeds the device binding limit.");
        }
        var entity = Own(world, Wgpu.CreateBuffer(device,
            new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst }), acquired);
        var staging = ArrayPool<T>.Shared.Rent(System.Math.Min(16384, System.Math.Max(1, source.Length)));
        try {
            for (var offset = 0; offset < source.Length;) {
                var count = System.Math.Min(staging.Length, source.Length - offset);
                for (var i = 0; i < count; i++) { staging[i] = pack(source[offset + i]); }
                Wgpu.WriteBuffer<T>(queue, entity.GetWgpu<WGPUBuffer>(), (ulong)offset * (ulong)sizeof(T), staging.AsSpan(0, count));
                offset += count;
            }
        }
        finally { ArrayPool<T>.Shared.Return(staging); }
        return entity;
    }
}
