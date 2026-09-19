using Sia.Math;

namespace Sia.Engine.Rendering;

public static class BoundsTransform
{
    public static Aabb Apply(Aabb box, float4x4 transform)
    {
        var center = math.mul(transform, new float4(box.Center, 1)).xyz;
        var half = box.HalfExtents;
        var extent = math.abs(transform.c0.xyz) * half.x + math.abs(transform.c1.xyz) * half.y + math.abs(transform.c2.xyz) * half.z;
        return new(center - extent, center + extent);
    }

    public static void Include(ref Aabb? bounds, Aabb? local, float4x4 transform)
    {
        if (local is not { } box) return;
        var world = Apply(box, transform);
        bounds = bounds is { } current ? Aabb.Union(current, world) : world;
    }
}
