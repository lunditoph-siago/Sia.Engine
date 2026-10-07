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
        ulong budget, int[] materialBatches, PbrStreamingSettings streaming,
        PbrLightmapAsset? lightmaps = null, PbrLightmapStream? pagedLightmaps = null, int lightmapOwnerBits = 0,
        bool preserveInstances = false)
    {
        _gpu = new(frame, budget);
        try {
            CompactVertices = lightmaps is not null || pagedLightmaps is not null;
            var localInstances = source.Bootstrap.HasDynamicInstances || preserveInstances;
            var mapping = CompactVertices ? CreateStreamLightmapMetadata(source, lightmaps, pagedLightmaps, streaming.GpuTraversal, lightmapOwnerBits, localInstances) : default;
            var metadataBytes = (ulong)(mapping.Metadata?.Length ?? 0) * 16;
            LightmapMetadataPrefixBytes = CompactVertices ? 16ul : 0;
            var offset = localInstances ? source.Instances.Length : 0;
            if (streaming.GpuTraversal || localInstances) {
                var instances = new PbrInstanceGpu[System.Math.Max(1, source.Instances.Length + (localInstances ? source.Bootstrap.Instances.Length : 0))];
                for (var i = 0; i < source.Instances.Length; i++) {
                    var instance = source.Instances.Span[i];
                    var norm = TransformNorm(instance.Transform);
                    instances[i] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.MaterialIndex, BitConverter.SingleToUInt32Bits(float.IsFinite(norm) ? norm : 1.0e30f),
                            checked((uint)(source.OpaqueSourceInstances.IsEmpty ? i : source.OpaqueSourceInstances.Span[i]) + 1), 0));
                }
                if (localInstances) for (var i = 0; i < source.Bootstrap.Instances.Length; i++) {
                    var instance = source.Bootstrap.Instances.Span[i];
                    instances[offset + i] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.Material, 0,
                            checked((uint)(source.BootstrapSourceInstances.IsEmpty ? offset + i : source.BootstrapSourceInstances.Span[i]) + 1),
                            instance.Dynamic ? 1u : 0u));
                }
                if (localInstances) _instanceData = instances;
                Instances = _gpu.Upload<PbrInstanceGpu>(instances);
            }
            var arenaBudget = budget - _gpu.Bytes;
            using var bootstrap = new PbrGpuScene(frame, source.Bootstrap, layout, arenaBudget, materialBatches, Instances,
                lightmaps, pagedLightmaps, compactVertices: CompactVertices,
                sourceInstances: source.BootstrapSourceInstances.ToArray().Select(i => source.StaticSourceInstances.Span[i]).ToArray(),
                instanceOffset: offset, lightmapOwnerBits: lightmapOwnerBits, preserveInstances: preserveInstances,
                authoredInstances: source.BootstrapSourceInstances.IsEmpty
                    ? Enumerable.Range(offset, source.Bootstrap.Instances.Length).ToArray() : source.BootstrapSourceInstances);
            if (localInstances) {
                _residentSource = source.Bootstrap;
                _instanceOffset = offset;
                _sourceInstanceCount = source.SourceInstanceCount;
                _sourceSlots = source.BootstrapSourceInstances.IsEmpty ? null
                    : source.BootstrapSourceInstances.ToArray().Select((slot, i) => (slot, i)).ToDictionary(p => p.slot, p => p.i);
                _enabled = bootstrap._enabled;
                _unscaledErrors = bootstrap._unscaledErrors;
                _drawSlots = bootstrap._drawSlots;
            }
            var shape = StreamGeometryShape(source, streaming.GpuTraversal, CompactVertices);
            ulong rootsV = bootstrap.VertexCount + shape.RootVertices, rootsT = bootstrap.TriangleCount + shape.RootTriangles;
            var vertexBytes = CompactVertices ? lightmapOwnerBits > 0 ? 36u : 40u : 48u;
            var rootVertexBytes = CompactVertices ? CompactVertexBytes(checked((uint)rootsV), true, lightmapOwnerBits) : rootsV * vertexBytes;
            var rootBytes = checked(rootVertexBytes + (rootsT * 12) + metadataBytes);
            if (rootBytes > arenaBudget)
                throw new ArgumentException($"Stream roots/bootstrap require {rootBytes} bytes, exceeding remaining geometry budget {arenaBudget}.");
            var detailBytes = System.Math.Min(streaming.DetailBytes, arenaBudget - rootBytes);
            var detail = StreamDetailCapacity(detailBytes, shape.DetailVertices, shape.DetailTriangles, vertexBytes);
            var maxBinding = _gpu.Limits.MaxStorageBufferBindingSize;
            if (metadataBytes >= maxBinding) throw new NotSupportedException("Lightmap metadata exceeds the geometry storage binding limit.");
            var vertexCapacity = System.Math.Min(rootsV + detail.Vertices, (maxBinding - metadataBytes) / vertexBytes);
            var triangleCapacity = System.Math.Min(rootsT + detail.Triangles, maxBinding / 12);
            var capacityBytes = CompactVertices ? CompactVertexBytes(checked((uint)vertexCapacity), true, lightmapOwnerBits) : vertexCapacity * vertexBytes;
            if (capacityBytes + triangleCapacity * 12 + metadataBytes > arenaBudget) {
                if (vertexCapacity > rootsV) vertexCapacity--;
                else if (triangleCapacity > rootsT) triangleCapacity--;
            }
            if (vertexCapacity < rootsV || triangleCapacity < rootsT)
                throw new NotSupportedException("Stream roots exceed WebGPU storage binding limits.");
            _arena = new(frame, checked((uint)System.Math.Max(1ul, vertexCapacity)), checked((uint)System.Math.Max(1ul, triangleCapacity)), arenaBudget, metadataBytes, vertexBytes);
            VertexCount = _arena.VertexCapacity;
            GeometryVertexBytes = _arena.VertexDataBytes;
            TriangleCount = OpaqueTriangles = _arena.TriangleCapacity;
            Vertices = _arena.Vertices;
            Topology = _arena.Topology;
            if (metadataBytes > 0)
                Wgpu.WriteBuffer<float4>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), GeometryVertexBytes, mapping.Metadata!);
            if (bootstrap.VertexCount > 0 && bootstrap.TriangleCount > 0) {
                if (!_arena.TryReserve(bootstrap.VertexCount, bootstrap.TriangleCount, out var allocation)
                    || allocation.Vertices.Offset != 0 || allocation.Triangles.Offset != 0)
                    throw new InvalidOperationException("Unable to reserve conventional bootstrap.");
                using var temporary = new GpuResources(frame, 0);
                var encoder = temporary.Own(Wgpu.CreateCommandEncoder(temporary.Device));
                for (uint plane = 0; plane < (CompactVertices ? 2u : 3u); plane++)
                    Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * bootstrap.VertexCount * 16,
                        Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * VertexCount * 16, (ulong)bootstrap.VertexCount * 16);
                if (CompactVertices)
                    Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Vertices.GetWgpu<WGPUBuffer>(), (ulong)bootstrap.VertexCount * 32,
                        Vertices.GetWgpu<WGPUBuffer>(), (ulong)VertexCount * 32, (ulong)bootstrap.VertexCount * (lightmapOwnerBits > 0 ? 4ul : 8ul));
                Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), bootstrap.Topology.GetWgpu<WGPUBuffer>(), 0,
                    Topology.GetWgpu<WGPUBuffer>(), 0, (ulong)bootstrap.TriangleCount * 12);
                var commands = temporary.Own(Wgpu.FinishCommandEncoder(encoder.GetWgpu<WGPUCommandEncoder>(), WGPUCommandBufferDescriptor.Default));
                Wgpu.Submit(temporary.Queue, [commands.GetWgpu<WGPUCommandBuffer>()]);
            }
            Transparent = bootstrap.Transparent;
            Conventional = bootstrap.Opaque;
            Streaming = new(source, _arena, streaming, materialBatches, mapping.Pack, localInstances);
            if (streaming.GpuTraversal) Streaming.CreateHierarchy(frame, Instances);
            Opaque = Streaming.RootDraws;
            Bounds = source.Bounds;
            if (Transparent.Length > 0 || Conventional.Length > 0)
                Bounds = new(math.min(Bounds.Min, bootstrap.Bounds.Min), math.max(Bounds.Max, bootstrap.Bounds.Max));
            var entries = new List<WGPUBindGroupEntry> { GpuBinding.Buffer(0, Vertices), GpuBinding.Buffer(1, Topology) };
            if (streaming.GpuTraversal || localInstances) entries.Add(GpuBinding.Buffer(2, Instances));
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

    internal static (ulong RootVertices, ulong RootTriangles, ulong DetailVertices, ulong DetailTriangles)
        StreamGeometryShape(PbrSceneStream source, bool shared, bool mapped)
    {
        var assets = new HashSet<(int Owner, int Geometry)>();
        for (var i = 0; i < source.Instances.Length; i++) {
            var geometry = source.Instances.Span[i].AssetIndex;
            assets.Add((shared ? mapped ? geometry : -1 : i, geometry));
        }
        var pages = new HashSet<(int Owner, string Page)>();
        ulong rootVertices = 0, rootTriangles = 0, detailVertices = 0, detailTriangles = 0;
        // Count roots first: a page shared with a root must not consume detail capacity.
        for (var phase = 0; phase < 2; phase++) foreach (var asset in assets) {
            var tree = source.Hierarchies[asset.Geometry];
            var nodes = phase == 0 ? tree.Nodes.AsSpan(0, tree.Roots) : tree.Nodes.AsSpan(tree.Roots);
            foreach (var node in nodes) foreach (var part in node.Pages) {
                if (!pages.Add((asset.Owner, part.Id))) continue;
                var page = source.PageTable[part.Id];
                if (phase == 0) { rootVertices += (uint)page.Vertices; rootTriangles += (uint)page.Triangles; }
                else { detailVertices += (uint)page.Vertices; detailTriangles += (uint)page.Triangles; }
            }
        }
        return (rootVertices, rootTriangles, detailVertices, detailTriangles);
    }

    internal static (ulong Vertices, ulong Triangles) StreamDetailCapacity(ulong budget, ulong vertices, ulong triangles, uint vertexBytes)
    {
        if (vertices == 0 || triangles == 0) return default;
        var bytes = checked(vertices * vertexBytes + triangles * 12);
        budget = System.Math.Min(budget, bytes);
        // Multiply before dividing without overflowing for large, valid cooked scenes.
        return ((ulong)((UInt128)budget * vertices / bytes), (ulong)((UInt128)budget * triangles / bytes));
    }
}
