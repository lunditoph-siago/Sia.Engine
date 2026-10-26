using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Explicit bounded one-bounce reflection work; the renderer owns view targets and execution.</summary>
public sealed record PbrReflectionSettings
{
    public uint MaximumSteps { get; init; } = 512;
    public uint MaximumShadowSteps { get; init; } = 256;
    public int MaximumLocalLights { get; init; } = 4;
    public float MaximumDistance { get; init; } = 100;
    public float RayBias { get; init; } = .002f;
    public float MaximumRoughness { get; init; } = .65f;
    public PbrReflectionHistorySettings? History { get; init; } = new();

    public void Validate()
    {
        History?.Validate();
        if (MaximumSteps is < 1 or > 65536 || MaximumShadowSteps is < 1 or > 65536 || MaximumLocalLights is < 0 or > 8
            || !float.IsFinite(MaximumDistance) || MaximumDistance <= 0 || MaximumDistance > 1e6f
            || !float.IsFinite(RayBias) || RayBias <= 0 || RayBias >= MaximumDistance
            || !float.IsFinite(MaximumRoughness) || MaximumRoughness < .045f || MaximumRoughness > 1)
            throw new ArgumentException("Invalid reflection ray/light/distance/roughness admission.");
    }

    internal float4[] Pack() => [new(MaximumDistance, RayBias, MaximumRoughness, 0), new(MaximumSteps, MaximumShadowSteps, MaximumLocalLights, 0)];
}

public readonly record struct PbrReflectionDirectionalLight(float3 TowardLight, float3 Radiance);
/// <summary>Local light in physical rendering units; Range zero has no cutoff within the bounded reflection work.</summary>
public readonly record struct PbrReflectionLocalLight(float3 Position, float Range, float3 Radiance,
    float3 Direction = default, float InnerCosine = 1, float OuterCosine = 0, bool Spot = false);

/// <summary>Valid black radiance remains a hit. Miss/incomplete signals must not revive history.</summary>
public readonly record struct PbrReflectionSample(float3 Radiance, PbrBvhHit Hit, bool Valid, bool Complete);
