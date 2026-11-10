using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// Six ordinary perspective shadow layers keep the existing depth-compare path
// and cache semantics; a point light selects a face by the dominant ray axis.
internal static class PbrPointShadow
{
    internal const int BaseLayers = 7;
    internal const int Faces = 6;
    internal const int MaximumLights = 4;
    internal const int LayerBits = 5;
    internal const uint LayerMask = (1u << LayerBits) - 1;
    internal const float NearPlane = .05f;

    internal static float DepthCoefficient(float range)
    {
        if (!float.IsFinite(range) || range <= NearPlane)
            throw new ArgumentException("Point shadow range must exceed its finite near plane.", nameof(range));
        return NearPlane * range / (range - NearPlane);
    }

    internal static void ValidateBias(float constant, float slope)
    {
        if (!float.IsFinite(constant) || constant < 0 || !float.IsFinite(slope) || slope < 0)
            throw new ArgumentException("Point shadow receiver biases must be finite nonnegative world distances.");
    }

    internal static int LayerCount(int pointCapacity)
    {
        if (pointCapacity is < 0 or > MaximumLights)
            throw new ArgumentOutOfRangeException(nameof(pointCapacity));
        return BaseLayers + pointCapacity * Faces;
    }

    internal static uint PackInstance(uint instance, uint layer)
    {
        if (layer > LayerMask) throw new ArgumentOutOfRangeException(nameof(layer));
        if (instance > uint.MaxValue >> LayerBits) throw new ArgumentOutOfRangeException(nameof(instance));
        return (instance << LayerBits) | layer;
    }

    internal static int Face(float3 lightToSurface)
    {
        if (!math.all(math.isfinite(lightToSurface)) || math.all(lightToSurface == 0))
            throw new ArgumentException("A cube shadow ray must be finite and nonzero.", nameof(lightToSurface));
        var magnitude = math.abs(lightToSurface);
        if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) return lightToSurface.x >= 0 ? 0 : 1;
        if (magnitude.y >= magnitude.z) return lightToSurface.y >= 0 ? 2 : 3;
        return lightToSurface.z >= 0 ? 4 : 5;
    }

    internal static float3 Direction(int face) => face switch {
        0 => new(1, 0, 0), 1 => new(-1, 0, 0),
        2 => new(0, 1, 0), 3 => new(0, -1, 0),
        4 => new(0, 0, 1), 5 => new(0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    internal static float4x4 ViewProjection(float3 position, int face, float range)
    {
        if (!math.all(math.isfinite(position)) || !float.IsFinite(range) || range <= NearPlane)
            throw new ArgumentException("Point shadows require a finite position and range greater than .05.");
        var direction = Direction(face);
        var up = face switch { 2 => new float3(0, 0, -1), 3 => new(0, 0, 1), _ => new(0, 1, 0) };
        var transform = new AffineTransform(position, quaternion.LookRotation(-direction, up));
        return CascadeSplitting.ComputeSpotViewProj(transform, MathF.PI / 4, range);
    }
}
