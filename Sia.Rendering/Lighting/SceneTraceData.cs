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

/// <summary>
/// Borrowed, repeatable triangle input. Count and indexed values must stay stable
/// throughout construction; the completed trace retains none of the source.
/// </summary>
public interface ISceneTraceTriangleSource
{
    int Count { get; }
    SceneTraceTriangle this[int index] { get; }
}

public sealed partial class SceneTraceData
{
    private readonly record struct Centroid(double X, double Y, double Z);
    private readonly record struct TriangleBounds(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
    {
        public TriangleBounds(float3 minimum, float3 maximum)
            : this(minimum.x, minimum.y, minimum.z, maximum.x, maximum.y, maximum.z) { }
        public float3 Minimum => new(MinX, MinY, MinZ);
        public float3 Maximum => new(MaxX, MaxY, MaxZ);
    }

    private readonly ref struct SpanTriangles(ReadOnlySpan<SceneTraceTriangle> triangles) : ISceneTraceTriangleSource
    {
        private readonly ReadOnlySpan<SceneTraceTriangle> _triangles = triangles;
        public int Count => _triangles.Length;
        public SceneTraceTriangle this[int index] => _triangles[index];
    }

    public ReadOnlyMemory<float4> Packed { get; }
    public ReadOnlyMemory<byte> Identity { get; }
    public Aabb Bounds { get; }
    public int TriangleCount { get; }

    /// <summary>Worst-case packed bytes without vertex/material sharing, for a fixed triangle capacity.</summary>
    public static ulong MaximumPackedBytes(int triangles)
    {
        if (triangles is < 1 or > 4_000_000) throw new ArgumentOutOfRangeException(nameof(triangles));
        return checked((2ul + (ulong)NodeCount(triangles) * 3 + (ulong)triangles * 6) * 16);
    }

    public SceneTraceData(
        ReadOnlySpan<SceneTraceTriangle> triangles,
        ulong maximumBytes = 128ul * 1024 * 1024,
        ReadOnlySpan<byte> sceneIdentity = default)
        : this(BuildPacked(new SpanTriangles(triangles), maximumBytes, sceneIdentity), sceneIdentity) { }

    /// <summary>Builds from a borrowed indexed value source without a full triangle snapshot.</summary>
    public static SceneTraceData Create<TSource>(TSource triangles,
        ulong maximumBytes = 128ul * 1024 * 1024, ReadOnlySpan<byte> sceneIdentity = default)
        where TSource : struct, ISceneTraceTriangleSource, allows ref struct
        => new(BuildPacked(triangles, maximumBytes, sceneIdentity), sceneIdentity);

    private SceneTraceData((float4[] Packed, int Triangles) built, ReadOnlySpan<byte> sceneIdentity)
    {
        Packed = built.Packed;
        Bounds = new(built.Packed[2].xyz, built.Packed[3].xyz);
        Identity = sceneIdentity.IsEmpty ? SHA256.HashData(MemoryMarshal.AsBytes(built.Packed.AsSpan())) : sceneIdentity.ToArray();
        TriangleCount = built.Triangles;
    }

    private static (float4[] Packed, int Triangles) BuildPacked<TSource>(TSource triangles,
        ulong maximumBytes, ReadOnlySpan<byte> sceneIdentity)
        where TSource : struct, ISceneTraceTriangleSource, allows ref struct
    {
        var count = triangles.Count;
        if (count is < 1 or > 4_000_000)
            throw new ArgumentException("Transport requires 1..4000000 triangles.", nameof(triangles));
        if (!sceneIdentity.IsEmpty && sceneIdentity.Length != 32)
            throw new ArgumentException("Scene identity must contain 32 bytes.", nameof(sceneIdentity));
        var nodeRecords = checked(NodeCount(count) * 3);
        var minimumBytes = checked((2ul + (ulong)nodeRecords + (ulong)count + 5) * 16);
        if (minimumBytes > maximumBytes)
            throw new ArgumentException("Transport cannot fit its configured packed budget.", nameof(maximumBytes));
        // Deduplication tables die before the hierarchy scratch is allocated.
        var packed = PackGeometry(triangles, count, nodeRecords, maximumBytes);
        var ordered = BuildHierarchy(triangles, count, packed.AsSpan(2, nodeRecords));
        ReorderTriangles(packed.AsSpan(2 + nodeRecords, count), ordered);
        return (packed, count);
    }

    private static float4[] PackGeometry<TSource>(TSource triangles, int count, int nodeRecords, ulong maximumBytes)
        where TSource : struct, ISceneTraceTriangleSource, allows ref struct
    {
        var surfaces = new Dictionary<(float3 Albedo, float3 Emission, bool DoubleSided), int>();
        var vertices = new Dictionary<(float X, float Y, float Z), int>();
        for (var i = 0; i < count; i++) {
            var t = triangles[i];
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
            surfaces.TryAdd((t.Albedo, t.Emission, t.DoubleSided), surfaces.Count);
            vertices.TryAdd((t.A.x, t.A.y, t.A.z), vertices.Count);
            vertices.TryAdd((t.B.x, t.B.y, t.B.z), vertices.Count);
            vertices.TryAdd((t.C.x, t.C.y, t.C.z), vertices.Count);
        }
        var recordCount = checked(2 + nodeRecords + count + vertices.Count + (surfaces.Count * 2));
        var packedBytes = checked((ulong)recordCount * 16);
        if (packedBytes > maximumBytes)
            throw new ArgumentException($"Packed transport needs {packedBytes} bytes; budget {maximumBytes}.");
        var nodeOffset = 2;
        var triangleOffset = nodeOffset + nodeRecords;
        var vertexOffset = triangleOffset + count;
        var surfaceOffset = vertexOffset + vertices.Count;
        var packed = GC.AllocateUninitializedArray<float4>(recordCount);
        packed[0] = new(nodeRecords / 3, triangleOffset, count, surfaceOffset);
        packed[1] = new(vertexOffset, 0, 0, 0);
        for (var i = 0; i < count; i++) {
            var t = triangles[i];
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
        return packed;
    }

    private static int[] BuildHierarchy<TSource>(TSource triangles, int count, Span<float4> nodes)
        where TSource : struct, ISceneTraceTriangleSource, allows ref struct
    {
        // Cache only bounds/centroids; indexed sources need not repeat world transforms per node.
        var bounds = GC.AllocateUninitializedArray<TriangleBounds>(count);
        var centroids = GC.AllocateUninitializedArray<Centroid>(count);
        var indices = new int[count];
        for (var i = 0; i < count; i++) {
            var t = triangles[i];
            bounds[i] = new(math.min(t.A, math.min(t.B, t.C)), math.max(t.A, math.max(t.B, t.C)));
            centroids[i] = new((double)t.A.x + t.B.x + t.C.x,
                (double)t.A.y + t.B.y + t.C.y, (double)t.A.z + t.B.z + t.C.z);
            indices[i] = i;
        }
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
        var nextNode = 0;
        BuildNodes(bounds, indices, nodes, comparers, 0, indices.Length, ref nextNode);
        Debug.Assert(nextNode == nodes.Length);
        // Recursive sorts partition contiguous ranges; final indices already have leaf order.
        return indices;
    }

    private static void ReorderTriangles(Span<float4> triangles, Span<int> order)
    {
        // Destination-to-source permutation, using each cycle once and one saved record.
        for (var i = 0; i < order.Length; i++) {
            if (order[i] < 0) continue;
            var saved = triangles[i];
            var current = i;
            while (true) {
                var next = order[current];
                order[current] = ~next;
                if (next == i) { triangles[current] = saved; break; }
                triangles[current] = triangles[next];
                current = next;
            }
        }
    }

    private static void BuildNodes(ReadOnlySpan<TriangleBounds> input, Span<int> indices,
        Span<float4> nodes, Comparison<int>[] comparers, int start, int count, ref int nextNode)
    {
        var node = nextNode;
        nextNode += 3;
        var lo = new float3(float.PositiveInfinity);
        var hi = new float3(float.NegativeInfinity);
        var end = start + count;
        for (var i = start; i < end; i++) {
            ref readonly var t = ref input[indices[i]];
            lo = math.min(lo, t.Minimum);
            hi = math.max(hi, t.Maximum);
        }
        if (count > 8) {
            var extentX = (double)hi.x - lo.x;
            var extentY = (double)hi.y - lo.y;
            var extentZ = (double)hi.z - lo.z;
            var axis = extentX >= extentY && extentX >= extentZ ? 0 : extentY >= extentZ ? 1 : 2;
            indices.Slice(start, count).Sort(comparers[axis]);
            var left = count / 2;
            BuildNodes(input, indices, nodes, comparers, start, left, ref nextNode);
            BuildNodes(input, indices, nodes, comparers, start + left, count - left, ref nextNode);
        }
        nodes[node] = new(lo, nextNode / 3); // escape node, stackless traversal
        nodes[node + 1] = new(hi, start);
        nodes[node + 2] = new(count <= 8 ? count : 0, 0, 0, 0);
    }

    private static int NodeCount(int triangles)
        => triangles <= 8 ? 1 : checked(1 + NodeCount(triangles / 2) + NodeCount(triangles - (triangles / 2)));
}
