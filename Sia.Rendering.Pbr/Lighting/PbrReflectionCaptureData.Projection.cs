using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrReflectionCaptureData
{
    /// <summary>Finite box projection and inside-edge influence. Outside receivers return false; invalid rays throw.</summary>
    public bool TryProject(float3 receiver, float3 direction, out float3 projectedDirection, out float influence)
    {
        projectedDirection = default; influence = 0;
        if (!math.all(math.isfinite(receiver)) || !math.all(math.isfinite(direction)) || math.all(direction == 0))
            throw new ArgumentException("Box projection needs a finite receiver and nonzero finite direction.");
        if (math.any(receiver < Bounds.Min) || math.any(receiver > Bounds.Max)) return false;
        var distance = double.PositiveInfinity; var weight = 1.0;
        for (var axis = 0; axis < 3; axis++) {
            var ray = (double)direction[axis]; var origin = (double)receiver[axis];
            if (ray != 0) distance = System.Math.Min(distance,
                ((ray > 0 ? (double)Bounds.Max[axis] : Bounds.Min[axis]) - origin) / ray);
            weight = System.Math.Min(weight, System.Math.Min((origin - Bounds.Min[axis]) / ((double)Position[axis] - Bounds.Min[axis]),
                ((double)Bounds.Max[axis] - origin) / ((double)Bounds.Max[axis] - Position[axis])));
        }
        var projected = (double3)receiver + (double3)direction * distance - (double3)Position;
        var length = math.length(projected);
        if (!double.IsFinite(distance) || !double.IsFinite(length) || length <= 0) return false;
        projectedDirection = (float3)(projected / length);
        var t = (float)System.Math.Clamp(weight, 0, 1); influence = t * t * (3 - 2 * t);
        return true;
    }
}
