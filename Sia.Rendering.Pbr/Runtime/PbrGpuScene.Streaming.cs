using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    private readonly StreamPageArena? _arena;

    internal PbrStreamResidency? Streaming { get; }

    public PbrGpuScene(in GpuFrame frame, PbrSceneStream source, Entity layout,
        ulong budget, int[] materialBatches, PbrStreamingSettings streaming)
    {
        _gpu = new(frame, 0);
        try {
            using var bootstrap = new PbrGpuScene(frame, source.Bootstrap, layout, budget, materialBatches);
            ulong rootsV = bootstrap.VertexCount, rootsT = bootstrap.TriangleCount;
            foreach (var instance in source.Instances.Span) {
                var tree = source.Hierarchies[instance.AssetIndex];
                var ids = tree.Nodes.Take(tree.Roots).SelectMany(n => n.Pages).Select(p => p.Id).Distinct(StringComparer.Ordinal);
                foreach (var id in ids) {
                    rootsV += (uint)source.PageTable[id].Vertices;
                    rootsT += (uint)source.PageTable[id].Triangles;
                }
            }
            var rootBytes = checked((rootsV * 48) + (rootsT * 12));
            if (rootBytes > budget)
                throw new ArgumentException($"Stream roots/bootstrap require {rootBytes} bytes, exceeding geometry budget {budget}.");
            var detailBytes = System.Math.Min(streaming.DetailBytes, budget - rootBytes);
            var maxBinding = _gpu.Limits.MaxStorageBufferBindingSize;
            var vertexCapacity = System.Math.Min(rootsV + (detailBytes * 4 / 5 / 48), maxBinding / 48);
            var triangleCapacity = System.Math.Min(rootsT + (detailBytes / 5 / 12), maxBinding / 12);
            if (vertexCapacity < rootsV || triangleCapacity < rootsT)
                throw new NotSupportedException("Stream roots exceed WebGPU storage binding limits.");
            _arena = new(frame, checked((uint)System.Math.Max(1ul, vertexCapacity)), checked((uint)System.Math.Max(1ul, triangleCapacity)), budget);
            VertexCount = _arena.VertexCapacity;
            TriangleCount = OpaqueTriangles = _arena.TriangleCapacity;
            Vertices = _arena.Vertices;
            Topology = _arena.Topology;
            if (bootstrap.VertexCount > 0 && bootstrap.TriangleCount > 0) {
                if (!_arena.TryReserve(bootstrap.VertexCount, bootstrap.TriangleCount, out var allocation)
                    || allocation.Vertices.Offset != 0 || allocation.Triangles.Offset != 0)
                    throw new InvalidOperationException("Unable to reserve transparent bootstrap.");
                using var temporary = new GpuResources(frame, 0);
                var encoder = temporary.Own(Wgpu.CreateCommandEncoder(temporary.Device));
                for (uint plane = 0; plane < 3; plane++)
                    Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * bootstrap.VertexCount * 16,
                        Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * VertexCount * 16, (ulong)bootstrap.VertexCount * 16);
                Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Topology.GetWgpu<WGPUBuffer>(), 0,
                    Topology.GetWgpu<WGPUBuffer>(), 0, (ulong)bootstrap.TriangleCount * 12);
                var commands = temporary.Own(Wgpu.FinishCommandEncoder(encoder.GetWgpu<WGPUCommandEncoder>(), WGPUCommandBufferDescriptor.Default));
                Wgpu.Submit(temporary.Queue, [commands.GetWgpu<WGPUCommandBuffer>()]);
            }
            Transparent = bootstrap.Transparent;
            Streaming = new(source, _arena, streaming, materialBatches);
            if (streaming.GpuTraversal) Streaming.CreateHierarchy(frame);
            Opaque = Streaming.RootDraws;
            Bounds = source.Bounds;
            if (Transparent.Length > 0)
                Bounds = new(math.min(Bounds.Min, bootstrap.Bounds.Min), math.max(Bounds.Max, bootstrap.Bounds.Max));
            Group = GpuBinding.Group(_gpu, layout, [GpuBinding.Buffer(0, Vertices), GpuBinding.Buffer(1, Topology)]);
        }
        catch {
            Streaming?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Streaming?.Hierarchy?.Dispose();
            _gpu.Dispose();
            _arena?.Dispose();
            throw;
        }
    }
}
