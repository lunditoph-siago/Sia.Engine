using System.Runtime.Intrinsics;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Six clip planes packed into two 128-bit groups, shared by native and WASM SIMD.</summary>
public readonly struct FrustumCuller
{
    private readonly PlaneGroup _sides, _depth;

    public FrustumCuller(in Frustum frustum)
    {
        _sides = new(frustum.Left, frustum.Right, frustum.Bottom, frustum.Top);
        _depth = new(frustum.Near, frustum.Far, frustum.Near, frustum.Far);
    }

    public bool Intersects(Aabb bounds) => _sides.Intersects(bounds) && _depth.Intersects(bounds);

    private readonly struct PlaneGroup(Plane a, Plane b, Plane c, Plane d)
    {
        private readonly Vector128<float> _x = Vector128.Create(a.Normal.x, b.Normal.x, c.Normal.x, d.Normal.x);
        private readonly Vector128<float> _y = Vector128.Create(a.Normal.y, b.Normal.y, c.Normal.y, d.Normal.y);
        private readonly Vector128<float> _z = Vector128.Create(a.Normal.z, b.Normal.z, c.Normal.z, d.Normal.z);
        private readonly Vector128<float> _w = Vector128.Create(a.Distance, b.Distance, c.Distance, d.Distance);

        public bool Intersects(Aabb bounds)
        {
            var x = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(_x, Vector128<float>.Zero),
                Vector128.Create(bounds.Max.x), Vector128.Create(bounds.Min.x));
            var y = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(_y, Vector128<float>.Zero),
                Vector128.Create(bounds.Max.y), Vector128.Create(bounds.Min.y));
            var z = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(_z, Vector128<float>.Zero),
                Vector128.Create(bounds.Max.z), Vector128.Create(bounds.Min.z));
            // Plain SIMD multiply/add avoids requiring fused arithmetic on the browser target.
            var dx = _x * x; var dy = _y * y; var dz = _z * z;
            var distance = (dx + dy) + (dz + _w);
            var tolerance = Vector128.Create(-1e-5f) * (Vector128.Abs(dx) + Vector128.Abs(dy)
                + Vector128.Abs(dz) + Vector128.Abs(_w) + Vector128.Create(1f));
            // Keep borderline and nonfinite inputs rather than introducing false negatives.
            return Vector128.LessThan(distance, tolerance).ExtractMostSignificantBits() == 0;
        }
    }
}
