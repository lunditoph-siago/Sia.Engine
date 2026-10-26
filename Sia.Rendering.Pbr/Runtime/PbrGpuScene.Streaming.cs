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
            var sharedInstances = streaming.GpuTraversal || source.RequiresInstancing;
            var shape = RootShape(source, sharedInstances);
            var required = checked(shape.Vertices * StreamGeometryPage.VertexBytes + shape.Triangles * 12 + shape.Instances);
            if (required > budget) throw new ArgumentException($"Stream roots/bootstrap/instances require {required} bytes, exceeding budget {budget}.");
            if (shape.Instances > _gpu.Limits.MaxStorageBufferBindingSize)
                throw new NotSupportedException("Stream instance table exceeds storage binding limits.");
            if (sharedInstances) {
                var instances = new PbrInstanceGpu[checked(source.Instances.Length + source.TransparentInstances.Length)];
                for (var i = 0; i < source.Instances.Length; i++) {
                    var instance = source.Instances.Span[i];
                    var norm = TransformNorm(instance.Transform);
                    if (!float.IsFinite(norm)) throw new ArgumentException("Instance LOD norm exceeds GPU float precision.", nameof(source));
                    instances[i] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.MaterialIndex, BitConverter.SingleToUInt32Bits(norm), 0, 0));
                }
                for (var i = 0; i < source.TransparentInstances.Length; i++) {
                    var instance = source.TransparentInstances[i];
                    var norm = TransformNorm(instance.Transform);
                    if (!float.IsFinite(norm)) throw new ArgumentException("Instance LOD norm exceeds GPU float precision.", nameof(source));
                    instances[source.Instances.Length + i] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.Material, BitConverter.SingleToUInt32Bits(norm), 0, 0));
                }
                _instanceMotion = new(source, instances);
                Instances = _gpu.Upload<PbrInstanceGpu>(instances.Length == 0 ? [default] : instances);
            }
            var arenaBudget = budget - _gpu.Bytes;
            using var bootstrap = new PbrGpuScene(frame, source, layout, arenaBudget, materialBatches, Instances);
            var rootsV = shape.Vertices; var rootsT = shape.Triangles;
            var rootBytes = checked((rootsV * StreamGeometryPage.VertexBytes) + (rootsT * 12));
            if (rootBytes > arenaBudget)
                throw new ArgumentException($"Stream roots/bootstrap require {rootBytes} bytes, exceeding remaining geometry budget {arenaBudget}.");
            var detailBytes = System.Math.Min(streaming.DetailBytes, arenaBudget - rootBytes);
            var maxBinding = _gpu.Limits.MaxStorageBufferBindingSize;
            var vertexCapacity = System.Math.Min(rootsV + (detailBytes * 4 / 5 / StreamGeometryPage.VertexBytes), maxBinding / StreamGeometryPage.VertexBytes);
            var triangleCapacity = System.Math.Min(rootsT + (detailBytes / 5 / 12), maxBinding / 12);
            if (sharedInstances && !streaming.GpuTraversal && source.Instances.Length > 0)
                triangleCapacity = System.Math.Min(triangleCapacity, (uint.MaxValue - 1ul) / (ulong)source.Instances.Length);
            if (vertexCapacity < rootsV || triangleCapacity < rootsT)
                throw new NotSupportedException("Stream roots exceed storage binding or CPU visibility addressing limits.");
            _arena = new(frame, checked((uint)System.Math.Max(1ul, vertexCapacity)), checked((uint)System.Math.Max(1ul, triangleCapacity)), arenaBudget);
            VertexCount = _arena.VertexCapacity;
            TriangleCount = OpaqueTriangles = _arena.TriangleCapacity;
            if (sharedInstances && !streaming.GpuTraversal) {
                var logicalTriangles = checked((ulong)source.Instances.Length * TriangleCount);
                if (logicalTriangles >= uint.MaxValue)
                    throw new NotSupportedException("CPU shared instance triangles exceed visibility ID addressing capacity.");
                OpaqueTriangles = (uint)logicalTriangles;
                var rows = _instanceMotion!.Rows;
                for (var i = 0; i < source.Instances.Length; i++) {
                    var material = rows[i].Material;
                    material.z = checked((uint)((ulong)i * TriangleCount));
                    material.w = TriangleCount;
                    rows[i] = rows[i] with { Material = material };
                }
                if (source.Instances.Length != 0)
                    Wgpu.WriteBuffer<PbrInstanceGpu>(_gpu.Queue, Instances.GetWgpu<WGPUBuffer>(), 0,
                        rows.AsSpan(0, source.Instances.Length));
            }
            Vertices = _arena.Vertices;
            Topology = _arena.Topology;
            if (bootstrap.VertexCount > 0 && bootstrap.TriangleCount > 0) {
                if (!_arena.TryReserve(bootstrap.VertexCount, bootstrap.TriangleCount, out var allocation)
                    || allocation.Vertices.Offset != 0 || allocation.Triangles.Offset != 0)
                    throw new InvalidOperationException("Unable to reserve transparent bootstrap.");
                using var temporary = new GpuResources(frame, 0);
                var encoder = temporary.Own(Wgpu.CreateCommandEncoder(temporary.Device));
                for (uint plane = 0; plane < StreamGeometryPage.VertexPlanes; plane++)
                    Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * bootstrap.VertexCount * 16,
                        Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * VertexCount * 16, (ulong)bootstrap.VertexCount * StreamGeometryPage.PlaneStride(plane));
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
            if (sharedInstances) entries.Add(GpuBinding.Buffer(2, Instances));
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
