using System.Buffers;
using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PbrInstanceGpu(float4x4 Transform, float4x4 NormalTransform, uint4 Material)
{
    public uint4 Geometry { get; init; }
}

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
        uint Instance)
    {
        public float3 Center => Bounds.Center;
        public uint SourceInstance { get; init; } = Instance;
        public ReadOnlyMemory<MeshRasterLod> Lods { get; init; }
        public float ErrorScale { get; init; } = 1;
        public uint InstanceCount { get; init; } = 1;
        public uint TriangleOffset { get; init; }
    }

    private readonly GpuResources _gpu;

    public Entity Vertices { get; }
    public Entity Topology { get; }
    public Entity Instances { get; }
    public Entity Group { get; }
    public Entity Attributes { get; }
    private readonly Entity _rasterGroup;
    public Entity RasterGroup => _rasterGroup == default ? Group : _rasterGroup;
    public uint VertexPackingMode { get; }

    public uint VertexCount { get; }
    public uint VertexPlanes { get; } = StreamGeometryPage.VertexPlanes;
    public uint TriangleOffset { get; }
    public uint SingleSidedTriangles { get; }
    public uint OpaqueTriangles { get; }
    public uint TriangleCount { get; }

    public Aabb Bounds { get; private set; }
    public Draw[] Transparent { get; }
    public Draw[] Opaque { get; private set; }

    public ulong Bytes => _gpu.Bytes + (_arena?.Bytes ?? 0) + (Streaming?.Hierarchy?.Bytes ?? 0);

    private PbrGpuScene(in GpuFrame frame, PbrSceneStream source, Entity layout, ulong budget, int[] materialBatches, Entity instances)
        : this(frame, source.Materials, source.TransparentInstances, source.TransparentGeometry.Length,
            g => new(source.TransparentGeometry[g]), layout, budget, materialBatches, instances, (uint)source.Instances.Length) { }

    private PbrGpuScene(in GpuFrame frame, ReadOnlyMemory<PbrMaterialAsset> materials,
        ReadOnlyMemory<PbrSceneInstance> sourceInstances, int geometryCount, Func<int, PbrResidentGeometry> prepareGeometry,
        Entity layout, ulong budget, int[] materialBatches, Entity instanceBuffer, uint instanceBase = 0)
    {
        _gpu = new(frame, budget);
        var geometries = new PbrResidentGeometry?[geometryCount];
        uint[]? vertexScratch = null;
        uint[]? indexScratch = null;
        try {
            if ((ulong)instanceBase + (ulong)sourceInstances.Length > (1ul << 24) + 1)
                throw new ArgumentOutOfRangeException(nameof(sourceInstances), "Packed instance indices must be exact Float32 integers.");
            for (var g = 0; g < geometries.Length; g++)
                geometries[g] = prepareGeometry(g);
            var totalVertices = 0;
            var totalTriangles = 0;
            var localInstances = instanceBuffer != default;
            var counted = new HashSet<int>();
            foreach (var instance in sourceInstances.Span) {
                if (localInstances && !counted.Add(instance.Geometry)) continue;
                var geometry = geometries[instance.Geometry]!;
                totalVertices = checked(totalVertices + geometry.VertexCount);
                totalTriangles = checked(totalTriangles + (int)geometry.TriangleCount);
            }
            var vertexBytes = checked((ulong)totalVertices * StreamGeometryPage.VertexBytes);
            var indexBytes = checked((ulong)totalTriangles * 12);
            if (vertexBytes > _gpu.Limits.MaxStorageBufferBindingSize || indexBytes > _gpu.Limits.MaxStorageBufferBindingSize
                || vertexBytes + indexBytes > budget)
                throw new ArgumentException($"Baked resident geometry needs {vertexBytes + indexBytes} bytes; budget {budget}.");
            VertexCount = (uint)totalVertices;
            TriangleOffset = 0;
            Vertices = _gpu.Buffer(vertexBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.Vertex | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
            Topology = _gpu.Buffer(indexBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.Index | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
            const int vertexBatch = 16384;
            const int indexBatch = 65536;
            vertexScratch = ArrayPool<uint>.Shared.Rent(vertexBatch * StreamVertexEncoding.Words);
            indexScratch = ArrayPool<uint>.Shared.Rent(indexBatch);
            void FlushVertices(int count, uint first)
            {
                if (count == 0) return;
                for (var plane = 0; plane < StreamGeometryPage.VertexPlanes; plane++) {
                    var stride = StreamGeometryPage.PlaneStride((uint)plane);
                    var at = checked((ulong)plane * (uint)totalVertices * 16 + (ulong)first * stride);
                    Wgpu.WriteBuffer<uint>(_gpu.Queue, Vertices.GetWgpu<WGPUBuffer>(), at,
                        vertexScratch.AsSpan(plane * vertexBatch * 4, count * (int)(stride / 4)));
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
                var m = materials.Span[sourceInstances.Span[i].Material];
                return m.AlphaBlend ? 2 : m.DoubleSided ? 1 : 0;
            }
            var order = Enumerable.Range(0, sourceInstances.Length).OrderBy(Bin)
                .ThenBy(i => materialBatches[sourceInstances.Span[i].Material]);
            uint vertexOffset = 0, triangleOffset = 0;
            var shared = new Dictionary<int, uint>();
            foreach (var i in order) {
                var instance = sourceInstances.Span[i];
                var material = materials.Span[instance.Material];
                var geometry = geometries[instance.Geometry]!;
                var transform = localInstances ? float4x4.identity : instance.Transform;
                var normal = math.transpose(math.inverse(transform));
                var upload = !localInstances || !shared.ContainsKey(instance.Geometry);
                var drawFirst = triangleOffset;
                if (!upload) drawFirst = shared[instance.Geometry];
                for (var first = 0; upload && first < geometry.VertexCount;) {
                    var count = System.Math.Min(vertexBatch - bufferedVertices, geometry.VertexCount - first);
                    geometry.PackVertices(vertexScratch, first, count,
                        transform, normal, instance.Material, checked(instanceBase + (uint)i), vertexBatch, bufferedVertices);
                    first += count;
                    bufferedVertices += count;
                    if (bufferedVertices == vertexBatch) {
                        FlushVertices(bufferedVertices, uploadedVertices);
                        uploadedVertices += (uint)bufferedVertices;
                        bufferedVertices = 0;
                    }
                }
                if (upload) geometry.WriteIndices(ref indexWriter, vertexOffset);
                var triangles = geometry.TriangleCount;
                var bounds = BoundsTransform.Apply(geometry.Bounds, instance.Transform);
                minimum = math.min(minimum, bounds.Min);
                maximum = math.max(maximum, bounds.Max);
                if (triangles != 0)
                    (material.AlphaBlend ? transparent : opaque).Add(new(drawFirst, geometry.TriangleCount,
                        instance.Material, bounds, material.DoubleSided, drawFirst * 3, checked(instanceBase + (uint)i)) { SourceInstance = (uint)i });
                if (upload) {
                    shared.TryAdd(instance.Geometry, triangleOffset);
                    vertexOffset += (uint)geometry.VertexCount;
                    triangleOffset += triangles;
                }
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
            if (instanceBuffer != default) entries.Add(GpuBinding.Buffer(2, instanceBuffer));
            Group = GpuBinding.Group(_gpu, layout, CollectionsMarshal.AsSpan(entries));
        }
        catch {
            _gpu.Dispose();
            throw;
        }
        finally {
            if (indexScratch is not null) ArrayPool<uint>.Shared.Return(indexScratch);
            if (vertexScratch is not null) ArrayPool<uint>.Shared.Return(vertexScratch);
            foreach (var geometry in geometries) geometry?.Dispose();
        }
    }

    internal static float TransformNorm(float4x4 m)
    {
        var a = math.abs((double3)m.c0.xyz);
        var b = math.abs((double3)m.c1.xyz);
        var c = math.abs((double3)m.c2.xyz);
        var column = System.Math.Max(a.x + a.y + a.z, System.Math.Max(b.x + b.y + b.z, c.x + c.y + c.z));
        var rows = a + b + c;
        var norm = System.Math.Sqrt(column * System.Math.Max(rows.x, System.Math.Max(rows.y, rows.z)));
        var rounded = (float)norm;
        return rounded < norm ? MathF.BitIncrement(rounded) : rounded;
    }

    public void Dispose()
    {
        _instanceMotion = null; _pendingInstanceMotion = null;
        Streaming?.Hierarchy?.Dispose();
        _gpu.Dispose();
        _arena?.Dispose();
    }
}
