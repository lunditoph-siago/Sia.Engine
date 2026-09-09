using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private Aabb?[] _assetBounds = [];
    internal Aabb? ShadowBounds { get; private set; }

    private static Aabb? PatchBounds(MeshPatchTree tree)
    {
        Aabb? bounds = null;
        foreach (var root in tree.Nodes.Span[..tree.RootCount]) {
            bounds = bounds is { } current ? Aabb.Union(current, root.Bounds) : root.Bounds;
        }
        return bounds;
    }

    private static Aabb? GeometryBounds(MeshletRasterData geometry)
    {
        Aabb? bounds = null;
        foreach (var vertex in geometry.Vertices.Span) {
            bounds = bounds is { } current
                ? new(math.min(current.Min, vertex.Position), math.max(current.Max, vertex.Position))
                : new(vertex.Position, vertex.Position);
        }
        return bounds;
    }

    private static void IncludeBounds(ref Aabb? bounds, Aabb? local, float4x4 transform)
    {
        if (local is not { } box) { return; }
        var center = math.mul(transform, new float4(box.Center, 1)).xyz;
        var half = box.HalfExtents;
        var extent = math.abs(transform.c0.xyz) * half.x + math.abs(transform.c1.xyz) * half.y + math.abs(transform.c2.xyz) * half.z;
        var world = new Aabb(center - extent, center + extent);
        bounds = bounds is { } current ? Aabb.Union(current, world) : world;
    }
}
