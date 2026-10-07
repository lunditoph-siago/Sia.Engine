using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Lighting for one-bounce probe transport, also usable as an immutable baked reference.</summary>
public readonly record struct DiffuseProbeLighting(
    ProceduralSky Sky, float3 TowardLight, float3 LightRadiance, float MaximumDistance = 1000)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Sky);
        static bool Color(float3 value) => math.all(math.isfinite(value) & (value >= 0));
        if (!Color(Sky.Horizon) || !Color(Sky.Zenith) || !Color(Sky.Ground) || !Color(Sky.SunRadiance)
            || !float.IsFinite(math.lengthsq(Sky.SunDirection)) || math.lengthsq(Sky.SunDirection) < 1e-8f
            || !float.IsFinite(Sky.Intensity) || Sky.Intensity < 0
            || !float.IsFinite(Sky.SunExponent) || Sky.SunExponent < 1)
            throw new ArgumentException("Invalid probe sky lighting.");
        if (!float.IsFinite(MaximumDistance) || MaximumDistance <= 0
            || !float.IsFinite(math.lengthsq(TowardLight)) || math.lengthsq(TowardLight) < 1e-12f
            || !math.all(math.isfinite(LightRadiance) & (LightRadiance >= 0)))
            throw new ArgumentException("Invalid probe transport lighting.");
    }

    internal void Write(Span<float4> sky, Span<float4> light)
    {
        sky[0] = new(Sky.Horizon, Sky.Intensity);
        sky[1] = new(Sky.Zenith, Sky.SunExponent);
        sky[2] = new(Sky.Ground, 0);
        sky[3] = new(math.normalize(Sky.SunDirection), 0);
        sky[4] = new(Sky.SunRadiance, 0);
        light[0] = new(math.normalize(TowardLight), MaximumDistance);
        light[1] = new(LightRadiance, 0);
    }
}
