using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// Bounded history shared by geometry residency and instance updates. A null box
// means that no spatial proof is available, so every consumer must invalidate.
internal sealed class VisibilityChanges
{
    private readonly Aabb?[] _bounds = new Aabb?[256];
    public ulong Version { get; private set; }

    public void Add(Aabb? bounds) => _bounds[(int)(++Version % (uint)_bounds.Length)] = bounds;

    public bool IntersectsSince(ulong version, float4x4 projection)
    {
        if (version > Version || Version - version > (ulong)_bounds.Length) return true;
        for (var revision = version + 1; revision <= Version; revision++) {
            if (_bounds[(int)(revision % (uint)_bounds.Length)] is not { } box || Intersects(box, projection)) return true;
        }
        return false;
    }

    internal static bool Intersects(Aabb box, float4x4 projection)
    {
        // Homogeneous clip planes, including the WebGPU near plane z >= 0.
        // Keep nonfinite and borderline boxes: false negatives invalidate images.
        Span<float4> corners = stackalloc float4[8];
        var scale = 1f;
        for (var i = 0; i < 8; i++) {
            var p = math.mul(projection, new float4((i & 1) == 0 ? box.Min.x : box.Max.x,
                (i & 2) == 0 ? box.Min.y : box.Max.y, (i & 4) == 0 ? box.Min.z : box.Max.z, 1));
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) || !float.IsFinite(p.w)) return true;
            corners[i] = p;
            scale = MathF.Max(scale, MathF.Max(MathF.Max(MathF.Abs(p.x), MathF.Abs(p.y)), MathF.Max(MathF.Abs(p.z), MathF.Abs(p.w))));
        }
        for (var plane = 0; plane < 6; plane++) {
            var outside = true;
            foreach (var p in corners) {
                var distance = plane switch { 0 => p.w + p.x, 1 => p.w - p.x, 2 => p.w + p.y,
                    3 => p.w - p.y, 4 => p.z, _ => p.w - p.z };
                if (distance >= -scale * 1e-5f) { outside = false; break; }
            }
            if (outside) return false;
        }
        return true;
    }
}
