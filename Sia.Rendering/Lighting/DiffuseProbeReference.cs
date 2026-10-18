using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Borrowed frozen transport and lighting inputs; contains no asset acquisition or frame effects.</summary>
public sealed record DiffuseProbeReference(SceneTraceData Scene, ProceduralSky Sky, float3 TowardLight,
    float3 LightRadiance, uint Samples, float MaximumDistance, float RayBias)
{
    internal void Validate(SceneTraceData live)
    {
        ArgumentNullException.ThrowIfNull(Scene); ArgumentNullException.ThrowIfNull(Sky); Sky.ValidateData();
        if (!live.IsInstanced || !Scene.IsInstanced || !live.Geometry.Equals(Scene.Geometry)
            || Samples is < 16 or > 1024 || !float.IsFinite(MaximumDistance) || MaximumDistance <= 0
            || !float.IsFinite(RayBias) || RayBias <= 0 || RayBias >= MaximumDistance
            || !math.all(math.isfinite(TowardLight)) || !float.IsFinite(math.lengthsq(TowardLight)) || math.lengthsq(TowardLight) <= 1e-8f
            || !math.all(math.isfinite(LightRadiance)) || math.any(LightRadiance < 0))
            throw new ArgumentException("Invalid paired probe reference geometry or lighting inputs.");
    }
}
