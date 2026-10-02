using Sia;
using Sia.Math;
using Sia.WebGPU;
using System.Runtime.InteropServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    private readonly StreamPageArena? _arena;

    internal PbrStreamResidency? Streaming { get; }

    public PbrGpuScene(in GpuFrame frame, PbrSceneStream source, Entity layout,
        ulong budget, int[] materialBatches, PbrStreamingSettings streaming)
    {
        _gpu = new(frame, budget);
        try {
            if (streaming.GpuTraversal) {
                var instances = new PbrInstanceGpu[System.Math.Max(1, source.Instances.Length)];
                for (var i = 0; i < source.Instances.Length; i++) {
                    var instance = source.Instances.Span[i];
                    var norm = TransformNorm(instance.Transform);
                    instances[i] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.MaterialIndex, BitConverter.SingleToUInt32Bits(float.IsFinite(norm) ? norm : 1.0e30f), 0, 0));
                }
                Instances = _gpu.Upload<PbrInstanceGpu>(instances);
            }
            var arenaBudget = budget - _gpu.Bytes;
            using var bootstrap = new PbrGpuScene(frame, source.Bootstrap, layout, arenaBudget, materialBatches, Instances);
            ulong rootsV = bootstrap.VertexCount, rootsT = bootstrap.TriangleCount;
            var sharedRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var instance in source.Instances.Span) {
                var tree = source.Hierarchies[instance.AssetIndex];
                var ids = tree.Nodes.Take(tree.Roots).SelectMany(n => n.Pages).Select(p => p.Id).Distinct(StringComparer.Ordinal);
                foreach (var id in ids) {
                    if (streaming.GpuTraversal && !sharedRoots.Add(id)) continue;
                    rootsV += (uint)source.PageTable[id].Vertices;
                    rootsT += (uint)source.PageTable[id].Triangles;
                }
            }
            var rootBytes = checked((rootsV * 48) + (rootsT * 12));
            if (rootBytes > arenaBudget)
                throw new ArgumentException($"Stream roots/bootstrap require {rootBytes} bytes, exceeding remaining geometry budget {arenaBudget}.");
            var detailBytes = System.Math.Min(streaming.DetailBytes, arenaBudget - rootBytes);
            var maxBinding = _gpu.Limits.MaxStorageBufferBindingSize;
            var vertexCapacity = System.Math.Min(rootsV + (detailBytes * 4 / 5 / 48), maxBinding / 48);
            var triangleCapacity = System.Math.Min(rootsT + (detailBytes / 5 / 12), maxBinding / 12);
            if (vertexCapacity < rootsV || triangleCapacity < rootsT)
                throw new NotSupportedException("Stream roots exceed WebGPU storage binding limits.");
            _arena = new(frame, checked((uint)System.Math.Max(1ul, vertexCapacity)), checked((uint)System.Math.Max(1ul, triangleCapacity)), arenaBudget);
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
            if (streaming.GpuTraversal) Streaming.CreateHierarchy(frame, Instances);
            Opaque = Streaming.RootDraws;
            Bounds = source.Bounds;
            if (Transparent.Length > 0)
                Bounds = new(math.min(Bounds.Min, bootstrap.Bounds.Min), math.max(Bounds.Max, bootstrap.Bounds.Max));
            var entries = new List<WGPUBindGroupEntry> { GpuBinding.Buffer(0, Vertices), GpuBinding.Buffer(1, Topology) };
            if (streaming.GpuTraversal) entries.Add(GpuBinding.Buffer(2, Instances));
            Group = GpuBinding.Group(_gpu, layout, CollectionsMarshal.AsSpan(entries));
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
