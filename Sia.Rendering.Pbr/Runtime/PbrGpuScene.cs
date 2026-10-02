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
    public uint TriangleOffset { get; }
    public uint SingleSidedTriangles { get; }
    public uint OpaqueTriangles { get; }
    public uint TriangleCount { get; }

    public Aabb Bounds { get; }
    public Draw[] Transparent { get; }
    public Draw[] Opaque { get; }

    public ulong Bytes => _gpu.Bytes + (_arena?.Bytes ?? 0) + (Streaming?.Hierarchy?.Bytes ?? 0);

    public PbrGpuScene(in GpuFrame frame, PbrSceneAsset source, Entity layout, ulong budget, int[] materialBatches, Entity instances = default)
    {
        _gpu = new(frame, budget);
        var geometries = new PbrResidentGeometry?[source.Geometry.Length];
        float4[]? vertexScratch = null;
        uint[]? indexScratch = null;
        try {
            for (var g = 0; g < geometries.Length; g++)
                geometries[g] = new(source.Geometry.Span[g].Build.Tree);
            var totalVertices = 0;
            var totalTriangles = 0;
            foreach (var instance in source.Instances.Span) {
                var geometry = geometries[instance.Geometry]!;
                totalVertices = checked(totalVertices + geometry.VertexCount);
                totalTriangles = checked(totalTriangles + (int)checked(geometry.Fine + geometry.Coarse));
            }
            var vertexBytes = checked((ulong)totalVertices * 48);
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
            vertexScratch = ArrayPool<float4>.Shared.Rent(vertexBatch * 3);
            indexScratch = ArrayPool<uint>.Shared.Rent(indexBatch);
            void FlushVertices(int count, uint first)
            {
                if (count == 0) return;
                for (var plane = 0; plane < 3; plane++) {
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
                for (var first = 0; first < geometry.VertexCount;) {
                    var count = System.Math.Min(vertexBatch - bufferedVertices, geometry.VertexCount - first);
                    geometry.PackVertices(vertexScratch.AsSpan(bufferedVertices), first, count,
                        instance.Transform, normal, instance.Material, vertexBatch);
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
                if (triangles != 0)
                    (material.AlphaBlend ? transparent : opaque).Add(new(triangleOffset, geometry.Fine,
                        instance.Material, bounds, material.DoubleSided, triangleOffset * 3, (uint)i,
                        triangleOffset + geometry.Fine, geometry.Coarse, geometry.Error * TransformNorm(instance.Transform)));
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
            if (instances != default) entries.Add(GpuBinding.Buffer(2, instances));
            Group = GpuBinding.Group(_gpu, layout, CollectionsMarshal.AsSpan(entries));
        }
        catch {
            _gpu.Dispose();
            throw;
        }
        finally {
            if (indexScratch is not null) ArrayPool<uint>.Shared.Return(indexScratch);
            if (vertexScratch is not null) ArrayPool<float4>.Shared.Return(vertexScratch);
            foreach (var geometry in geometries) geometry?.Dispose();
        }
    }

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
