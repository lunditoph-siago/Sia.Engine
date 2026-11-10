using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Viewport-clipped screen extent of a world AABB, independent of geometric LOD error.</summary>
public static class ProjectedBounds
{
    /// <summary>
    /// Returns the larger projected rectangle extent in pixels, bounded by the viewport.
    /// Uses WebGPU depth [0,w]. Near/far clipping occurs before perspective division;
    /// camera-inside/near-crossing boxes remain conservative without infinite demand.
    /// </summary>
    public static float PixelSize(Aabb bounds, in float4x4 viewProjection, uint width, uint height)
    {
        if (width == 0 || height == 0) return 0;
        var maximum = (float)System.Math.Max(width, height);
        Span<float4> corners = stackalloc float4[8];
        var commonOutside = 63;
        var allDepthInside = true;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (var corner = 0; corner < 8; corner++) {
            var point = new float4((corner & 1) == 0 ? bounds.Min.x : bounds.Max.x,
                (corner & 2) == 0 ? bounds.Min.y : bounds.Max.y,
                (corner & 4) == 0 ? bounds.Min.z : bounds.Max.z, 1);
            var clip = math.mul(viewProjection, point);
            // Invalid/overflowed input is conservatively bounded, never an Inf
            // priority or an accidental silent loss of otherwise visible detail.
            if (!math.all(math.isfinite(clip))) return maximum;
            corners[corner] = clip;
            var outside = (clip.x < -clip.w ? 1 : 0) | (clip.x > clip.w ? 2 : 0)
                | (clip.y < -clip.w ? 4 : 0) | (clip.y > clip.w ? 8 : 0)
                | (clip.z < 0 ? 16 : 0) | (clip.z > clip.w ? 32 : 0);
            commonOutside &= outside;
            if (clip.z >= 0 && clip.z <= clip.w) {
                if (!Include(clip, clip, 0, ref minX, ref minY, ref maxX, ref maxY)) return maximum;
            }
            else allDepthInside = false;
        }
        if (commonOutside != 0) return 0;
        // The AABB clipped by parallel camera near/far depth planes has vertices
        // only at original corners or intersections with these twelve edges.
        // Project that convex shape, then intersect its screen rectangle with the
        // viewport. This also handles a box containing the entire camera frustum.
        for (var axis = 0; !allDepthInside && axis < 3; axis++) {
            var bit = 1 << axis;
            for (var corner = 0; corner < 8; corner++) {
                if ((corner & bit) != 0) continue;
                var a = corners[corner]; var b = corners[corner | bit];
                double first = 0, last = 1;
                if (!Clip(a.z, b.z, ref first, ref last)
                    || !Clip((double)a.w - a.z, (double)b.w - b.z, ref first, ref last)) continue;
                if (!Include(a, b, first, ref minX, ref minY, ref maxX, ref maxY)
                    || !Include(a, b, last, ref minX, ref minY, ref maxX, ref maxY)) return maximum;
            }
        }
        if (double.IsPositiveInfinity(minX)) return 0;
        var x = System.Math.Max(0, System.Math.Min(1, maxX) - System.Math.Max(-1, minX)) * .5 * width;
        var y = System.Math.Max(0, System.Math.Min(1, maxY) - System.Math.Max(-1, minY)) * .5 * height;
        return (float)System.Math.Min(maximum, System.Math.Max(x, y));
    }

    private static bool Clip(double a, double b, ref double first, ref double last)
    {
        if (a < 0 && b < 0) return false;
        if (a >= 0 && b >= 0) return true;
        var intersection = a / (a - b);
        if (a < 0) first = System.Math.Max(first, intersection);
        else last = System.Math.Min(last, intersection);
        return first <= last;
    }

    private static bool Include(float4 a, float4 b, double t, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        var w = a.w + ((double)b.w - a.w) * t;
        // A valid finite perspective camera clips at a positive near distance.
        // Degenerate projective inputs remain conservative and finite as well.
        if (w <= 1e-30) return false;
        var x = (a.x + ((double)b.x - a.x) * t) / w;
        var y = (a.y + ((double)b.y - a.y) * t) / w;
        minX = System.Math.Min(minX, x); minY = System.Math.Min(minY, y);
        maxX = System.Math.Max(maxX, x); maxY = System.Math.Max(maxY, y);
        return true;
    }
}
