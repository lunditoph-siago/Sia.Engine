using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed record ProceduralSky
{
    public float3 Horizon { get; init; } = new(0.55f, 0.6f, 0.68f);
    public float3 Zenith { get; init; } = new(0.12f, 0.24f, 0.55f);
    public float3 Ground { get; init; } = new(0.08f, 0.08f, 0.07f);
    public float3 SunDirection { get; init; } = math.normalize(new float3(0.4f, 1.0f, 0.3f));
    public float3 SunRadiance { get; init; } = new(8.0f, 7.68f, 7.2f);
    public float SunExponent { get; init; } = 256.0f;
    public float Intensity { get; init; } = 1.0f;

    public void Validate()
    {
        ValidateColor(Horizon, nameof(Horizon));
        ValidateColor(Zenith, nameof(Zenith));
        ValidateColor(Ground, nameof(Ground));
        ValidateColor(SunRadiance, nameof(SunRadiance));
        var length = math.dot(SunDirection, SunDirection);
        if (!float.IsFinite(length) || length < 1e-8f) {
            throw new ArgumentOutOfRangeException(nameof(SunDirection));
        }
        if (!float.IsFinite(Intensity) || Intensity < 0) {
            throw new ArgumentOutOfRangeException(nameof(Intensity));
        }
        if (!float.IsFinite(SunExponent) || SunExponent < 1) {
            throw new ArgumentOutOfRangeException(nameof(SunExponent));
        }
    }

    public float3 Evaluate(float3 direction)
    {
        var up = System.Math.Clamp(direction.y, -1.0f, 1.0f);
        var sky = math.lerp(Horizon, Zenith, System.Math.Clamp(up, 0.0f, 1.0f));
        var blend = System.Math.Clamp((up + 0.15f) / 0.2f, 0.0f, 1.0f);
        var radiance = math.lerp(Ground, sky, blend * blend * (3.0f - 2.0f * blend));
        var sun = MathF.Max(math.dot(direction, math.normalize(SunDirection)), 0.0f);
        return (radiance + SunRadiance * MathF.Pow(sun, SunExponent)) * Intensity;
    }

    private static void ValidateColor(float3 value, string name)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)
            || value.x < 0 || value.y < 0 || value.z < 0) {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
