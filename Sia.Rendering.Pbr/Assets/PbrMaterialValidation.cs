using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal static class PbrMaterialValidation
{
    internal static bool ParametersValid(PbrMaterial p, float normalScale, float occlusionStrength,
        bool alphaBlend, float opacity, float transmission, float thickness)
        => math.all(math.isfinite(p.BaseColor)) && math.all(math.isfinite(p.EmissiveColor))
            && float.IsFinite(p.EmissiveStrength) && p.EmissiveStrength >= 0
            && float.IsFinite(p.Metallic) && p.Metallic is >= 0 and <= 1 && float.IsFinite(p.Roughness) && p.Roughness is >= 0 and <= 1
            && float.IsFinite(normalScale) && float.IsFinite(occlusionStrength) && occlusionStrength is >= 0 and <= 1
            && float.IsFinite(opacity) && opacity is >= 0 and <= 1 && float.IsFinite(transmission) && transmission is >= 0 and <= 1
            && float.IsFinite(thickness) && thickness is >= 0 and <= 10 && (transmission == 0 || alphaBlend);

    internal static bool TexturesValid(PbrTextureData? baseColor, PbrTextureData? normal, PbrTextureData? metallicRoughness,
        PbrTextureData? occlusion, PbrTextureData? emissive)
        => baseColor is not { Srgb: false } && emissive is not { Srgb: false }
            && normal is not { Srgb: true } && metallicRoughness is not { Srgb: true } && occlusion is not { Srgb: true };
}
