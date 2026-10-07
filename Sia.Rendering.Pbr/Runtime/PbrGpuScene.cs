using System.Buffers;
using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PbrInstanceGpu(float4x4 Transform, float4x4 NormalTransform, uint4 Material);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct PbrWorkGpu(uint Triangle, uint Instance);

internal sealed partial class PbrGpuScene : IDisposable
{
    public readonly record struct Draw(
        uint First,
        uint Count,
        int Material,
        Aabb Bounds,
        bool DoubleSided,
        uint FirstIndex,
        uint Instance,
        uint CoarseFirst,
        uint CoarseCount,
        float Error)
    {
        public float3 Center => Bounds.Center;
    }

    private readonly GpuResources _gpu;

    public Entity Vertices { get; }
    public Entity Topology { get; }
    public Entity Instances { get; }
    public Entity Group { get; }

    public uint VertexCount { get; }
    internal bool CompactVertices { get; }
    internal uint VertexPlanes => CompactVertices ? 2u : 3u;
    internal ulong GeometryVertexBytes { get; }
    internal ulong LightmapMetadataPrefixBytes { get; }
    public uint TriangleOffset { get; }
    public uint SingleSidedTriangles { get; }
    public uint OpaqueTriangles { get; }
    public uint TriangleCount { get; }

    public Aabb Bounds { get; private set; }
    internal bool LocalInstances => _residentSource is not null;
    internal ulong Revision { get; private set; }
    public Draw[] Transparent { get; }
    public Draw[] Opaque { get; }
    internal Draw[] Conventional { get; } = [];

    public ulong Bytes => _gpu.Bytes + (_arena?.Bytes ?? 0) + (Streaming?.Hierarchy?.Bytes ?? 0);

    public PbrGpuScene(in GpuFrame frame, PbrSceneAsset source, Entity layout, ulong budget, int[] materialBatches, Entity instances = default,
        PbrLightmapAsset? lightmaps = null, PbrLightmapStream? pagedLightmaps = null, bool compactVertices = true,
        ReadOnlyMemory<int> sourceInstances = default, int instanceOffset = 0, int lightmapOwnerBits = 0,
        bool preserveInstances = false, ReadOnlyMemory<int> authoredInstances = default)
    {
        _gpu = new(frame, budget);
        CompactVertices = compactVertices;
        var geometries = new PbrResidentGeometry?[source.Geometry.Length];
        float4[]? vertexScratch = null;
        uint[]? indexScratch = null;
        uint[]? compactScratch = null;
        int[]? chartScratch = null;
        ReadOnlyMemory<PbrLightmapReceiver> lightmapReceivers = lightmaps?.Receivers ?? pagedLightmaps?.Receivers ?? default;
        ReadOnlyMemory<PbrLightmapChart> lightmapCharts = lightmaps?.Charts ?? pagedLightmaps?.Charts ?? default;
        var planes = lightmapReceivers.IsEmpty ? 3 : 4;
        var charted = !lightmapCharts.IsEmpty;
        var receiverMappings = planes == 3 ? null : new float4[source.Instances.Length];
        var receiverIndices = lightmaps?.Encoding == PbrLightmapEncoding.L1Unorm8 || charted ? new int[source.Instances.Length] : null;
        var receiverCharts = charted ? lightmapCharts.ToArray().Select((chart, index) => (chart, index))
            .GroupBy(c => c.chart.StaticInstance).ToDictionary(g => g.Key, g => g.ToArray()) : null;
        if (planes == 4) {
            var mapping = lightmapReceivers.ToArray().Select((receiver, index) => (receiver, index))
                .ToDictionary(item => item.receiver.StaticInstance);
            var staticIndex = 0;
            for (var i = 0; i < source.Instances.Length; i++) {
                if (source.Instances.Span[i].Dynamic) continue;
                if (mapping.TryGetValue(sourceInstances.IsEmpty ? staticIndex : sourceInstances.Span[i], out var entry)) {
                    receiverMappings![i] = entry.receiver.ScaleBias;
                    if (receiverIndices is not null) receiverIndices[i] = entry.index;
                }
                staticIndex++;
            }
        }
        try {
            if (source.HasDynamicInstances || preserveInstances) {
                _residentSource = source;
                _instanceOffset = instanceOffset;
                _instanceData = new PbrInstanceGpu[System.Math.Max(1, instanceOffset + source.Instances.Length)];
                _enabled = Enumerable.Repeat(true, _instanceData.Length).ToArray();
                _unscaledErrors = new float[_instanceData.Length];
                _drawSlots = new (bool Transparent, int Index)[_instanceData.Length];
                for (var i = 0; i < source.Instances.Length; i++) {
                    var instance = source.Instances.Span[i];
                    _instanceData[i + instanceOffset] = new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                        new((uint)instance.Material, 0, checked((uint)(authoredInstances.IsEmpty ? i : authoredInstances.Span[i]) + 1),
                            instance.Dynamic ? 1u : 0u));
                    _drawSlots[i + instanceOffset] = (false, -1);
                }
                Instances = instances != default ? instances : _gpu.Upload<PbrInstanceGpu>(_instanceData);
            }
            for (var g = 0; g < geometries.Length; g++)
                geometries[g] = new(source.Geometry.Span[g].Build.Tree);
            var totalVertices = 0;
            var totalTriangles = 0;
            foreach (var instance in source.Instances.Span) {
                var geometry = geometries[instance.Geometry]!;
                totalVertices = checked(totalVertices + geometry.VertexCount);
                totalTriangles = checked(totalTriangles + (int)checked(geometry.Fine + geometry.Coarse));
            }
            var geometryBytes = CompactVertices ? CompactVertexBytes((uint)totalVertices, planes == 4, lightmapOwnerBits)
                : checked((ulong)totalVertices * (uint)planes * 16);
            GeometryVertexBytes = geometryBytes;
            var metadata = CreateLightmapMetadata(lightmaps, pagedLightmaps);
            var metadataCount = metadata.Length;
            var vertexBytes = geometryBytes + (ulong)metadataCount * 16;
            var indexBytes = checked((ulong)totalTriangles * 12);
            if (vertexBytes > _gpu.Limits.MaxStorageBufferBindingSize || indexBytes > _gpu.Limits.MaxStorageBufferBindingSize
                || vertexBytes + indexBytes > budget - _gpu.Bytes)
                throw new ArgumentException($"Baked resident geometry needs {vertexBytes + indexBytes} bytes; budget {budget}. "
                    + $"Vertices: {totalVertices}, {geometryBytes} bytes plus {metadataCount * 16L} metadata bytes; "
                    + $"indices: {indexBytes} bytes; storage binding limit: {_gpu.Limits.MaxStorageBufferBindingSize} bytes.");
            VertexCount = (uint)totalVertices;
            TriangleOffset = 0;
            Vertices = _gpu.Buffer(vertexBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.Vertex | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
            Topology = _gpu.Buffer(indexBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.Index | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
            if (metadata.Length > 0)
                Wgpu.WriteBuffer<float4>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), geometryBytes, metadata);
            if (charted) {
                chartScratch = ArrayPool<int>.Shared.Rent(lightmapReceivers.ToArray().Max(r => r.Resolution * r.Resolution));
            }
            const int vertexBatch = 16384;
            const int indexBatch = 65536;
            var lightmapWordStride = lightmapOwnerBits > 0 ? 1 : 2;
            if (CompactVertices) compactScratch = ArrayPool<uint>.Shared.Rent(vertexBatch * (planes == 4 ? 8 + lightmapWordStride : 8));
            else vertexScratch = ArrayPool<float4>.Shared.Rent(vertexBatch * planes);
            indexScratch = ArrayPool<uint>.Shared.Rent(indexBatch);
            void FlushVertices(int count, uint first)
            {
                if (count == 0) return;
                if (CompactVertices) {
                    for (var plane = 0; plane < 2; plane++)
                        Wgpu.WriteBuffer<uint>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), ((ulong)plane * (uint)totalVertices + first) * 16,
                            compactScratch.AsSpan(plane * vertexBatch * 4, count * 4));
                    if (planes == 4)
                        Wgpu.WriteBuffer<uint>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), (ulong)totalVertices * 32 + (ulong)first * (uint)lightmapWordStride * 4,
                            compactScratch.AsSpan(vertexBatch * 8, count * lightmapWordStride));
                    return;
                }
                for (var plane = 0; plane < planes; plane++) {
                    var at = checked((ulong)plane * (uint)totalVertices + first);
                    Wgpu.WriteBuffer<float4>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), at * 16,
                        vertexScratch.AsSpan(plane * vertexBatch, count));
                }
            }
            var indexWriter = new PbrResidentGeometry.IndexWriter(indexScratch.AsSpan(0, indexBatch),
                (values, first) => Wgpu.WriteBuffer<uint>(_gpu.Queue, Topology.GetWgpu<WGPUBuffer>(), (ulong)first * 4, values));
            var bufferedVertices = 0;
            uint uploadedVertices = 0;
            var transparent = new List<Draw>();
            var opaque = new List<Draw>();
            var minimum = new float3(float.PositiveInfinity);
            var maximum = new float3(float.NegativeInfinity);
            int Bin(int i)
            {
                var m = source.Materials.Span[source.Instances.Span[i].Material];
                return m.AlphaBlend ? 2 : m.DoubleSided ? 1 : 0;
            }
            var order = Enumerable.Range(0, source.Instances.Length).OrderBy(Bin)
                .ThenBy(i => materialBatches[source.Instances.Span[i].Material]);
            uint vertexOffset = 0, triangleOffset = 0;
            foreach (var i in order) {
                var instance = source.Instances.Span[i];
                var material = source.Materials.Span[instance.Material];
                var geometry = geometries[instance.Geometry]!;
                var normal = math.transpose(math.inverse(instance.Transform));
                var chartResolution = 0;
                if (charted && receiverMappings![i].x > 0) {
                    var receiver = lightmapReceivers.Span[receiverIndices![i]];
                    chartResolution = receiver.Resolution;
                    chartScratch!.AsSpan(0, chartResolution * chartResolution).Fill(-1);
                    foreach (var (chart, index) in receiverCharts![receiver.StaticInstance])
                        for (var y = chart.Y - receiver.Y; y < chart.Y - receiver.Y + chart.Height; y++)
                            chartScratch.AsSpan(y * chartResolution + chart.X - receiver.X, chart.Width).Fill(index);
                    geometry.ValidateLightmapCharts(chartScratch.AsSpan(0, chartResolution * chartResolution), chartResolution);
                }
                for (var first = 0; first < geometry.VertexCount;) {
                    var count = System.Math.Min(vertexBatch - bufferedVertices, geometry.VertexCount - first);
                    if (CompactVertices) geometry.PackCompactVertices(compactScratch, first, count,
                        LocalInstances ? float4x4.identity : instance.Transform,
                        LocalInstances ? float4x4.identity : normal, LocalInstances ? i + instanceOffset : instance.Material, vertexBatch,
                        planes == 3 ? null : receiverMappings![i], receiverIndices?[i] ?? 0,
                        chartResolution == 0 ? default : chartScratch.AsSpan(0, chartResolution * chartResolution), chartResolution, bufferedVertices, lightmapWordStride, lightmapOwnerBits);
                    else geometry.PackVertices(vertexScratch.AsSpan(bufferedVertices), first, count,
                        LocalInstances ? float4x4.identity : instance.Transform,
                        LocalInstances ? float4x4.identity : normal, LocalInstances ? i + instanceOffset : instance.Material, vertexBatch,
                        planes == 3 ? null : receiverMappings![i], receiverIndices?[i] ?? 0,
                        chartResolution == 0 ? default : chartScratch.AsSpan(0, chartResolution * chartResolution), chartResolution);
                    first += count;
                    bufferedVertices += count;
                    if (bufferedVertices == vertexBatch) {
                        FlushVertices(bufferedVertices, uploadedVertices);
                        uploadedVertices += (uint)bufferedVertices;
                        bufferedVertices = 0;
                    }
                }
                geometry.WriteIndices(ref indexWriter, vertexOffset);
                var triangles = checked(geometry.Fine + geometry.Coarse);
                var bounds = BoundsTransform.Apply(geometry.Bounds, instance.Transform);
                minimum = math.min(minimum, bounds.Min);
                maximum = math.max(maximum, bounds.Max);
                if (triangles != 0) {
                    var draws = material.AlphaBlend ? transparent : opaque;
                    if (LocalInstances) {
                        _drawSlots![i + instanceOffset] = (material.AlphaBlend, draws.Count);
                        _unscaledErrors![i + instanceOffset] = geometry.Error;
                    }
                    draws.Add(new(triangleOffset, geometry.Fine,
                        instance.Material, bounds, material.DoubleSided, triangleOffset * 3, (uint)(i + instanceOffset),
                        triangleOffset + geometry.Fine, geometry.Coarse, geometry.Error * TransformNorm(instance.Transform)));
                }
                vertexOffset += (uint)geometry.VertexCount;
                triangleOffset += triangles;
                if (Bin(i) == 0) SingleSidedTriangles = triangleOffset;
                if (Bin(i) <= 1) OpaqueTriangles = triangleOffset;
            }
            FlushVertices(bufferedVertices, uploadedVertices);
            indexWriter.Flush();
            TriangleCount = triangleOffset;
            Bounds = totalTriangles == 0 ? new(float3.zero, float3.zero) : new(minimum, maximum);
            Transparent = [.. transparent];
            Opaque = [.. opaque];
            var entries = new List<WGPUBindGroupEntry> {
                GpuBinding.Buffer(0, Vertices),
                GpuBinding.Buffer(1, Topology)
            };
            if (LocalInstances) entries.Add(GpuBinding.Buffer(2, Instances));
            else if (instances != default) entries.Add(GpuBinding.Buffer(2, instances));
            Group = GpuBinding.Group(_gpu, layout, CollectionsMarshal.AsSpan(entries));
        }
        catch {
            _gpu.Dispose();
            throw;
        }
        finally {
            if (indexScratch is not null) ArrayPool<uint>.Shared.Return(indexScratch);
            if (vertexScratch is not null) ArrayPool<float4>.Shared.Return(vertexScratch);
            if (compactScratch is not null) ArrayPool<uint>.Shared.Return(compactScratch);
            if (chartScratch is not null) ArrayPool<int>.Shared.Return(chartScratch);
            foreach (var geometry in geometries) geometry?.Dispose();
        }
    }

    internal static ulong CompactVertexBytes(uint vertices, bool lightmaps, int lightmapOwnerBits = 0)
        => (ulong)vertices * 32 + (lightmaps ? lightmapOwnerBits > 0 ? ((ulong)vertices + 3) / 4 * 16 : ((ulong)vertices + 1) / 2 * 16 : 0);

    internal static float TransformNorm(float4x4 m)
    {
        var a = math.abs(m.c0.xyz);
        var b = math.abs(m.c1.xyz);
        var c = math.abs(m.c2.xyz);
        var column = MathF.Max(a.x + a.y + a.z, MathF.Max(b.x + b.y + b.z, c.x + c.y + c.z));
        var rows = a + b + c;
        return MathF.Sqrt(column * MathF.Max(rows.x, MathF.Max(rows.y, rows.z)));
    }

    public void Dispose()
    {
        Streaming?.Hierarchy?.Dispose();
        _gpu.Dispose();
        _arena?.Dispose();
    }
}
