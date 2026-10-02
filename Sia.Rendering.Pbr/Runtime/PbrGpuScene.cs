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
        try {
            var geometries = source.Geometry.ToArray().Select(asset => {
                var tree = asset.Build.Tree;
                if (tree.RootCount == tree.Nodes.Length) {
                    var finest = tree.CopyFinestGeometry().Geometry;
                    return (Mesh: finest, Fine: (uint)finest.Indices.Length / 3, Coarse: 0u, Error: 0f);
                }
                var geometry = tree.CopyGeometry().Geometry;
                var indices = new List<uint>();
                foreach (var node in tree.Nodes.Span)
                    if (node.ChildCount == 0)
                        indices.AddRange(geometry.Indices.AsSpan(node.TriangleOffset * 3, node.TriangleCount * 3));
                var fine = (uint)indices.Count / 3;
                var error = 0f;
                foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                    indices.AddRange(geometry.Indices.AsSpan(node.TriangleOffset * 3, node.TriangleCount * 3));
                    error = MathF.Max(error, node.EstimatedSpatialError);
                }
                return (Mesh: geometry with {
                    Indices = [.. indices]
                }, Fine: fine, Coarse: ((uint)indices.Count / 3) - fine, Error: error);
            }).ToArray();
            var meshes = geometries.Select(g => g.Mesh).ToArray();
            var totalVertices = 0;
            var totalTriangles = 0;
            foreach (var instance in source.Instances.Span) {
                totalVertices = checked(totalVertices + meshes[instance.Geometry].Vertices.Length);
                totalTriangles = checked(totalTriangles + (meshes[instance.Geometry].Indices.Length / 3));
            }
            var vertexBytes = checked((ulong)totalVertices * 48);
            var indexBytes = checked((ulong)totalTriangles * 12);
            if (vertexBytes > _gpu.Limits.MaxStorageBufferBindingSize || indexBytes > _gpu.Limits.MaxStorageBufferBindingSize
                || vertexBytes + indexBytes > budget)
                throw new ArgumentException($"Baked resident geometry needs {vertexBytes + indexBytes} bytes; budget {budget}.");
            VertexCount = (uint)totalVertices;
            TriangleOffset = 0;
            var packed = new float4[checked(totalVertices * 3)];
            var topology = new uint[checked(totalTriangles * 3)];
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
                var mesh = meshes[instance.Geometry];
                var normal = math.transpose(math.inverse(instance.Transform));
                for (var v = 0; v < mesh.Vertices.Length; v++) {
                    var vertex = mesh.Vertices[v];
                    var position = math.mul(instance.Transform, new float4(vertex.Position, 1)).xyz;
                    var n = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
                    var t = math.mul(instance.Transform, new float4(vertex.Tangent.xyz, 0)).xyz;
                    var at = (int)vertexOffset + v;
                    packed[at] = new(position, n.x);
                    packed[totalVertices + at] = new(n.y, n.z, vertex.UV.x, vertex.UV.y);
                    packed[(totalVertices * 2) + at] = new(t, (vertex.Tangent.w < 0 ? -1 : 1) * (instance.Material + 1));
                }
                for (var index = 0; index < mesh.Indices.Length; index++)
                    topology[(triangleOffset * 3) + index] = vertexOffset + mesh.Indices[index];
                var count = (uint)mesh.Indices.Length / 3;
                var bounds = BoundsTransform.Apply(mesh.Bounds, instance.Transform);
                minimum = math.min(minimum, bounds.Min);
                maximum = math.max(maximum, bounds.Max);
                if (count != 0)
                    (material.AlphaBlend ? transparent : opaque).Add(new(triangleOffset, geometries[instance.Geometry].Fine, instance.Material, bounds, material.DoubleSided, triangleOffset * 3, (uint)i,
                        triangleOffset + geometries[instance.Geometry].Fine, geometries[instance.Geometry].Coarse,
                        geometries[instance.Geometry].Error * TransformNorm(instance.Transform)));
                vertexOffset += (uint)mesh.Vertices.Length;
                triangleOffset += count;
                if (Bin(i) == 0) SingleSidedTriangles = triangleOffset;
                if (Bin(i) <= 1) OpaqueTriangles = triangleOffset;
            }
            TriangleCount = triangleOffset;
            Vertices = _gpu.Upload<float4>(packed, WGPUBufferUsage.Storage | WGPUBufferUsage.Vertex | WGPUBufferUsage.CopySrc);
            Topology = _gpu.Upload<uint>(topology, WGPUBufferUsage.Storage | WGPUBufferUsage.Index | WGPUBufferUsage.CopySrc);
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
