using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed record ProceduralSky
{
    public float3 Horizon { get; init; } = new(0.55f, 0.6f, 0.68f);
    public float3 Zenith { get; init; } = new(0.12f, 0.24f, 0.55f);
    public float3 Ground { get; init; } = new(0.08f, 0.08f, 0.07f);
    public float3 SunDirection { get; init; } = math.normalize(new float3(0.4f, 1.0f, 0.3f));
    public float3 SunRadiance { get; init; } = new(8.0f, 7.68f, 7.2f);
    public float SunExponent { get; init; } = 256.0f;
    public float Intensity { get; init; } = 1.0f;

    [Conditional("DEBUG")]
    public void Validate()
    {
        Require(IsColor(Horizon), nameof(Horizon));
        Require(IsColor(Zenith), nameof(Zenith));
        Require(IsColor(Ground), nameof(Ground));
        Require(IsColor(SunRadiance), nameof(SunRadiance));
        var sunLength = math.dot(SunDirection, SunDirection);
        Require(float.IsFinite(sunLength) && sunLength >= 1e-8f, nameof(SunDirection));
        Require(float.IsFinite(Intensity) && Intensity >= 0, nameof(Intensity));
        Require(float.IsFinite(SunExponent) && SunExponent >= 1, nameof(SunExponent));
    }

    public float3 Evaluate(float3 direction)
    {
        var up = System.Math.Clamp(direction.y, -1.0f, 1.0f);
        var sky = math.lerp(Horizon, Zenith, System.Math.Clamp(up, 0.0f, 1.0f));
        var blend = System.Math.Clamp((up + 0.15f) / 0.2f, 0.0f, 1.0f);
        var radiance = math.lerp(Ground, sky, blend * blend * (3.0f - (2.0f * blend)));
        var sun = MathF.Max(math.dot(direction, math.normalize(SunDirection)), 0.0f);
        return (radiance + (SunRadiance * MathF.Pow(sun, SunExponent))) * Intensity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new ArgumentOutOfRangeException(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsColor(float3 value)
        => math.all(math.isfinite(value) & (value >= 0));
}
