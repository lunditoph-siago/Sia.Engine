using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public enum PbrSceneLightKind { Directional, Point, Spot }

/// <summary>Authored linear light coefficients in world space. Direction is propagation; range zero has no cutoff.</summary>
public readonly record struct PbrSceneLight(Guid Id, PbrSceneLightKind Kind, float3 Color, float Intensity,
    float3 Position, float3 Direction, float Range = 0, float InnerAngle = 0, float OuterAngle = 0, bool CastShadows = false);

public sealed partial class PbrSceneData
{
    private static void ValidateLights(ReadOnlySpan<PbrSceneLight> lights)
    {
        if (lights.Length > MaximumLights) throw new ArgumentException("Scene light count limit exceeded.");
        var ids = new HashSet<Guid>();
        foreach (var light in lights) {
            if (light.Id == Guid.Empty || !ids.Add(light.Id)
                || light.Kind is < PbrSceneLightKind.Directional or > PbrSceneLightKind.Spot
                || !math.all(math.isfinite(light.Color)) || math.any(light.Color < 0)
                || !float.IsFinite(light.Intensity) || light.Intensity < 0
                || !math.all(math.isfinite(light.Color * light.Intensity))
                || !math.all(math.isfinite(light.Position)) || !math.all(math.isfinite(light.Direction))
                || !float.IsFinite(light.Range) || light.Range < 0
                || !float.IsFinite(light.InnerAngle) || !float.IsFinite(light.OuterAngle))
                throw new ArgumentException("Invalid authored light identity or finite linear coefficients.");
            if (light.Kind != PbrSceneLightKind.Point) {
                var length = math.lengthsq(light.Direction);
                if (!float.IsFinite(length) || length < 1e-12f)
                    throw new ArgumentException("Authored directional and spot lights require a nonzero propagation vector.");
            }
            if (light.Kind == PbrSceneLightKind.Directional && light.Range != 0)
                throw new ArgumentException("Directional lights have no distance cutoff.");
            if (light.Kind == PbrSceneLightKind.Spot) {
                if ((light.Range > 0 && light.Range <= .05f) || light.InnerAngle < 0
                    || light.OuterAngle <= light.InnerAngle || light.OuterAngle >= MathF.PI / 2)
                    throw new ArgumentException("Spot lights require range zero or > .05 and 0 <= inner < outer < pi/2.");
            }
            else if (light.InnerAngle != 0 || light.OuterAngle != 0)
                throw new ArgumentException("Only spot lights have cone angles.");
        }
    }
}
