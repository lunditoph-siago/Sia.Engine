using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrMaterialAsset(
    PbrMaterial Parameters,
    PbrTextureData? BaseColor = null,
    PbrTextureData? Normal = null,
    PbrTextureData? MetallicRoughness = null,
    PbrTextureData? Occlusion = null,
    PbrTextureData? Emissive = null,
    float NormalScale = 1,
    float OcclusionStrength = 1,
    bool DoubleSided = false,
    bool AlphaBlend = false,
    float Opacity = 1,
    float Transmission = 0,
    float Thickness = 0);

public readonly record struct PbrSceneInstance(int Geometry, int Material, float4x4 Transform)
{
    public Guid AuthoredId { get; init; }
    public int MaterialSlot { get; init; } = -1;
}
