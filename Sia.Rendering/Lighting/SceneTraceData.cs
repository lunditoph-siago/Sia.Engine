using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct SceneTraceTriangle
{
    private readonly Components _a;
    private readonly Components _b;
    private readonly Components _c;
    private readonly Components _albedo;
    private readonly Components _emission;

    public float3 A { get => _a.Vector; init => _a = new(value); }
    public float3 B { get => _b.Vector; init => _b = new(value); }
    public float3 C { get => _c.Vector; init => _c = new(value); }
    public float3 Albedo { get => _albedo.Vector; init => _albedo = new(value); }
    public float3 Emission { get => _emission.Vector; init => _emission = new(value); }
    public bool DoubleSided { get; init; }

    public SceneTraceTriangle(float3 A, float3 B, float3 C, float3 Albedo, float3 Emission, bool DoubleSided)
    {
        _a = new(A);
        _b = new(B);
        _c = new(C);
        _albedo = new(Albedo);
        _emission = new(Emission);
        this.DoubleSided = DoubleSided;
    }

    public void Deconstruct(out float3 A, out float3 B, out float3 C,
        out float3 Albedo, out float3 Emission, out bool DoubleSided)
    {
        A = this.A;
        B = this.B;
        C = this.C;
        Albedo = this.Albedo;
        Emission = this.Emission;
        DoubleSided = this.DoubleSided;
    }

    private readonly record struct Components(float X, float Y, float Z)
    {
        public Components(float3 value) : this(value.x, value.y, value.z) { }
        public float3 Vector => new(X, Y, Z);
    }
}

public sealed class SceneTraceData
{
    private readonly record struct Centroid(double X, double Y, double Z);

    public ReadOnlyMemory<float4> Packed { get; }
    public ReadOnlyMemory<byte> Identity { get; }
    public Aabb Bounds { get; }
    public int TriangleCount { get; }

    public SceneTraceData(
        ReadOnlySpan<SceneTraceTriangle> triangles,
        ulong maximumBytes = 128ul * 1024 * 1024,
        ReadOnlySpan<byte> sceneIdentity = default)
    {
        if (triangles.Length == 0 || triangles.Length > 4_000_000)
            throw new ArgumentException("Transport requires 1..4000000 triangles.", nameof(triangles));
        if (!sceneIdentity.IsEmpty && sceneIdentity.Length != 32)
            throw new ArgumentException("Scene identity must contain 32 bytes.", nameof(sceneIdentity));
        var nodeCount = NodeCount(triangles.Length);
        var minimumBytes = checked((2ul + ((ulong)nodeCount * 3) + (ulong)triangles.Length + 5) * 16);
        if (minimumBytes > maximumBytes)
            throw new ArgumentException("Transport cannot fit its configured packed budget.", nameof(maximumBytes));
        var centroids = GC.AllocateUninitializedArray<Centroid>(triangles.Length);
        var surfaces = new Dictionary<(float3 Albedo, float3 Emission, bool DoubleSided), int>();
        var vertices = new Dictionary<(float X, float Y, float Z), int>();
        var triangleIndex = 0;
        foreach (ref readonly var t in triangles) {
            var ab = t.B - t.A;
            var ac = t.C - t.A;
            var area2 = math.lengthsq(math.cross(ab, ac));
            if (!math.all(math.isfinite(t.A))
                || !math.all(math.isfinite(t.B))
                || !math.all(math.isfinite(t.C))
                || !math.all(math.isfinite(t.Albedo) & (t.Albedo >= 0))
                || !math.all(math.isfinite(t.Emission) & (t.Emission >= 0))
                || !math.all(math.isfinite(ab))
                || !math.all(math.isfinite(ac))
                || !math.isfinite(area2)
                || area2 < 1e-16f)
                throw new ArgumentException("Invalid transport triangle.", nameof(triangles));
            centroids[triangleIndex++] = new((double)t.A.x + t.B.x + t.C.x,
                (double)t.A.y + t.B.y + t.C.y, (double)t.A.z + t.B.z + t.C.z);
            surfaces.TryAdd((t.Albedo, t.Emission, t.DoubleSided), surfaces.Count);
            vertices.TryAdd((t.A.x, t.A.y, t.A.z), vertices.Count);
            vertices.TryAdd((t.B.x, t.B.y, t.B.z), vertices.Count);
            vertices.TryAdd((t.C.x, t.C.y, t.C.z), vertices.Count);
        }
        var indices = new int[triangles.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        var nodes = new List<float4>(checked(nodeCount * 3));
        var ordered = new List<int>(triangles.Length);
        double Coordinate(int index, int axis)
        {
            ref readonly var t = ref centroids[index];
            return axis switch {
                0 => t.X,
                1 => t.Y,
                _ => t.Z
            };
        }
        int Compare(int a, int b, int axis)
        {
            var order = Coordinate(a, axis).CompareTo(Coordinate(b, axis));
            return order == 0 ? a.CompareTo(b) : order;
        }
        var comparers = new Comparison<int>[] {
            (a, b) => Compare(a, b, 0),
            (a, b) => Compare(a, b, 1),
            (a, b) => Compare(a, b, 2)
        };
        BuildNodes(triangles, indices, nodes, ordered, comparers, 0, indices.Length);
        Bounds = new(nodes[0].xyz, nodes[1].xyz);
        var recordCount = checked(2 + nodes.Count + ordered.Count + vertices.Count + (surfaces.Count * 2));
        var packedBytes = checked((ulong)recordCount * 16);
        if (packedBytes > maximumBytes)
            throw new ArgumentException($"Packed transport needs {packedBytes} bytes; budget {maximumBytes}.");
        var nodeOffset = 2;
        var triangleOffset = nodeOffset + nodes.Count;
        var vertexOffset = triangleOffset + ordered.Count;
        var surfaceOffset = vertexOffset + vertices.Count;
        var packed = GC.AllocateUninitializedArray<float4>(recordCount);
        packed[0] = new(nodes.Count / 3, triangleOffset, ordered.Count, surfaceOffset);
        packed[1] = new(vertexOffset, 0, 0, 0);
        nodes.CopyTo(packed, nodeOffset);
        for (var i = 0; i < ordered.Count; i++) {
            ref readonly var t = ref triangles[ordered[i]];
            packed[triangleOffset + i] = new(
                vertices[(t.A.x, t.A.y, t.A.z)],
                vertices[(t.B.x, t.B.y, t.B.z)],
                vertices[(t.C.x, t.C.y, t.C.z)],
                surfaces[(t.Albedo, t.Emission, t.DoubleSided)]);
        }
        foreach (var pair in vertices)
            packed[vertexOffset + pair.Value] = new(pair.Key.X, pair.Key.Y, pair.Key.Z, 0);
        foreach (var pair in surfaces) {
            var offset = surfaceOffset + (pair.Value * 2);
            var (Albedo, Emission, DoubleSided) = pair.Key;
            packed[offset] = new(Albedo, DoubleSided ? 1 : 0);
            packed[offset + 1] = new(Emission, 0);
        }
        Packed = packed;
        Identity = sceneIdentity.IsEmpty ? SHA256.HashData(MemoryMarshal.AsBytes(packed.AsSpan())) : sceneIdentity.ToArray();
        TriangleCount = triangles.Length;
    }

    private static void BuildNodes(ReadOnlySpan<SceneTraceTriangle> input, Span<int> indices,
        List<float4> nodes, List<int> ordered, Comparison<int>[] comparers, int start, int count)
    {
        var node = nodes.Count;
        nodes.Add(default);
        nodes.Add(default);
        nodes.Add(default);
        var lo = new float3(float.PositiveInfinity);
        var hi = new float3(float.NegativeInfinity);
        var end = start + count;
        for (var i = start; i < end; i++) {
            ref readonly var t = ref input[indices[i]];
            lo = math.min(lo, math.min(t.A, math.min(t.B, t.C)));
            hi = math.max(hi, math.max(t.A, math.max(t.B, t.C)));
        }
        var first = ordered.Count;
        if (count <= 8) {
            for (var i = start; i < end; i++)
                ordered.Add(indices[i]);
        }
        else {
            var extentX = (double)hi.x - lo.x;
            var extentY = (double)hi.y - lo.y;
            var extentZ = (double)hi.z - lo.z;
            var axis = extentX >= extentY && extentX >= extentZ ? 0 : extentY >= extentZ ? 1 : 2;
            indices.Slice(start, count).Sort(comparers[axis]);
            var left = count / 2;
            BuildNodes(input, indices, nodes, ordered, comparers, start, left);
            BuildNodes(input, indices, nodes, ordered, comparers, start + left, count - left);
        }
        nodes[node] = new(lo, nodes.Count / 3); // escape node, stackless traversal
        nodes[node + 1] = new(hi, first);
        nodes[node + 2] = new(count <= 8 ? count : 0, 0, 0, 0);
    }

    private static int NodeCount(int triangles)
        => triangles <= 8 ? 1 : checked(1 + NodeCount(triangles / 2) + NodeCount(triangles - (triangles / 2)));
}
