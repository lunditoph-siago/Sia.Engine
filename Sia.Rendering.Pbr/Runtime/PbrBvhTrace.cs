using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Parameter is t in origin + t * direction, not distance unless direction has unit length.</summary>
public readonly record struct PbrBvhHit(Guid InstanceId, int Instance, int SourceTriangle,
    int MaterialSlot, int Material, double Parameter, float3 Barycentric, bool FrontFace)
{
    /// <summary>World-space unit geometric normal in authored winding; back-face hits do not flip it.</summary>
    public float3 GeometricNormal { get; init; }
}

/// <summary>
/// Immutable CPU instance acceleration over acquired local BVHs. Build never copies/rebuilds mesh geometry.
/// Callers retain the scene's assets; GPU upload/traversal and opacity/shading policy are separate.
/// </summary>
public sealed partial class PbrBvhTrace
{
    private readonly record struct InstanceData(double3 Row0, double3 Row1, double3 Row2, double3 Translation, Aabb Bounds, bool Disabled)
    {
        public double3 LocalVector(double3 vector) => new(math.dot(Row0, vector), math.dot(Row1, vector), math.dot(Row2, vector));
    }
    private readonly record struct Node(Aabb Bounds, int Escape, int First, int Count);
    private readonly InstanceData[] _instances;
    private readonly int[] _order;
    private readonly Node[] _nodes;
    /// <summary>Borrowed authored geometry, bindings and identities. Runtime transforms belong to this trace snapshot.</summary>
    public PbrBvhScene Scene { get; }
    public int NodeCount => _nodes.Length;
    public Aabb Bounds => _nodes.Length == 0 ? new(float3.zero, float3.zero) : _nodes[0].Bounds;
    /// <summary>Logical acceleration array bytes, including topology shared by runtime snapshots; excludes scene/geometry, object headers and build scratch.</summary>
    public ulong PayloadBytes { get; }

    private PbrBvhTrace(PbrBvhScene scene, InstanceData[] instances, int[] order, Node[] nodes, ulong bytes)
    {
        Scene = scene; _instances = instances; _order = order; _nodes = nodes; PayloadBytes = bytes;
    }

    public static PbrBvhTrace Build(PbrBvhScene scene, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); cancellationToken.ThrowIfCancellationRequested();
        var count = scene.Instances.Length; var nodeCount = CountNodes(count);
        var bytes = (ulong)count * ((ulong)Unsafe.SizeOf<InstanceData>() + sizeof(int)) + (ulong)nodeCount * (ulong)Unsafe.SizeOf<Node>();
        if (bytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Instance acceleration exceeds its owned-array budget.");
        var instances = new InstanceData[count]; var order = new int[count]; var nodes = new Node[nodeCount];
        for (var i = 0; i < count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            instances[i] = CreateInstance(scene, i, scene.Instances[i].Transform);
            order[i] = i;
        }
        var comparisons = new Comparison<int>[3];
        for (var axis = 0; axis < 3; axis++) {
            var component = axis;
            comparisons[axis] = (a, b) => {
                var compared = Center(instances[a].Bounds, component).CompareTo(Center(instances[b].Bounds, component));
                return compared != 0 ? compared : scene.Instances[a].Id.CompareTo(scene.Instances[b].Id);
            };
        }
        var next = 0;
        void BuildNode(int first, int length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = next++; var bounds = instances[order[first]].Bounds;
            var lo = new double3(double.PositiveInfinity); var hi = new double3(double.NegativeInfinity);
            for (var i = first; i < first + length; i++) {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var item = instances[order[i]].Bounds; bounds = Aabb.Union(bounds, item);
                var center = new double3(Center(item, 0), Center(item, 1), Center(item, 2));
                lo = math.min(lo, center); hi = math.max(hi, center);
            }
            if (length <= 4) { nodes[node] = new(bounds, next, first, length); return; }
            var extent = hi - lo; var axis = extent.y > extent.x ? 1 : 0; if (extent.z > extent[axis]) axis = 2;
            order.AsSpan(first, length).Sort(comparisons[axis]);
            var left = length / 2; BuildNode(first, left); BuildNode(first + left, length - left);
            nodes[node] = new(bounds, next, 0, 0);
        }
        if (count != 0) BuildNode(0, count);
        cancellationToken.ThrowIfCancellationRequested();
        return new(scene, instances, order, nodes, bytes);
    }

    private static int CountNodes(int count) => count == 0 ? 0 : count <= 4 ? 1 : 1 + CountNodes(count / 2) + CountNodes(count - count / 2);
    private static double Center(Aabb bounds, int axis) => ((double)bounds.Min[axis] + bounds.Max[axis]) * .5;

    private static InstanceData CreateInstance(PbrBvhScene scene, int index, float4x4 transform)
    {
        if (!PbrSceneData.ValidTransform(transform))
            throw new ArgumentException("Instance requires a finite nonsingular positive affine transform.", nameof(transform));
        var a = (double3)transform.c0.xyz; var b = (double3)transform.c1.xyz; var c = (double3)transform.c2.xyz;
        var determinant = math.dot(a, math.cross(b, c));
        if (!double.IsFinite(determinant) || determinant <= 0)
            throw new ArgumentException("Instance has no positive affine inverse.", nameof(transform));
        var localBounds = scene.Geometry[scene.Bindings[scene.Instances[index].Binding].Geometry].Bounds;
        var bounds = TransformBounds(localBounds, transform);
        if (!math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)))
            throw new ArgumentException("Conservative transformed instance bounds must remain finite.", nameof(transform));
        return new(math.cross(b, c) / determinant, math.cross(c, a) / determinant,
            math.cross(a, b) / determinant, (double3)transform.c3.xyz, bounds, false);
    }

    private static Aabb TransformBounds(Aabb bounds, float4x4 transform)
    {
        var lo = (double3)transform.c3.xyz; var hi = lo;
        for (var axis = 0; axis < 3; axis++) {
            var column = (double3)transform[axis].xyz;
            var a = column * bounds.Min[axis]; var b = column * bounds.Max[axis];
            lo += math.min(a, b); hi += math.max(a, b);
        }
        // Expand at float precision after double interval arithmetic; bounds remain conservative at stored edges.
        return new(new(MathF.BitDecrement((float)lo.x), MathF.BitDecrement((float)lo.y), MathF.BitDecrement((float)lo.z)),
            new(MathF.BitIncrement((float)hi.x), MathF.BitIncrement((float)hi.y), MathF.BitIncrement((float)hi.z)));
    }
}
