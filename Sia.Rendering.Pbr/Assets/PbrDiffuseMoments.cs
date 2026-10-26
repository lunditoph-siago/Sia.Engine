using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Factor-independent, uniform-UV LOD0 diffuse moments and their finest-source fingerprints.</summary>
public sealed partial class PbrDiffuseMoments
{
    public float3 BaseColorMean { get; }
    public float3 BaseColorMetallicMean { get; }
    public string? BaseColorHash { get; }
    public string? MetallicRoughnessHash { get; }

    public PbrDiffuseMoments(float3 baseColorMean, float3 baseColorMetallicMean,
        string? baseColorHash, string? metallicRoughnessHash)
    {
        if (!math.all(math.isfinite(baseColorMean)) || !math.all(math.isfinite(baseColorMetallicMean))
            || math.any(baseColorMean < 0) || math.any(baseColorMean > 1)
            || math.any(baseColorMetallicMean < 0) || math.any(baseColorMetallicMean > baseColorMean))
            throw new ArgumentException("Invalid correlated diffuse moments.");
        static string? Hash(string? value) => value is null ? null
            : value.Length == 64 && value.All(char.IsAsciiHexDigit) ? value.ToUpperInvariant()
            : throw new ArgumentException("Diffuse moment sources require SHA-256 fingerprints.");
        BaseColorMean = baseColorMean;
        BaseColorMetallicMean = baseColorMetallicMean;
        BaseColorHash = Hash(baseColorHash);
        MetallicRoughnessHash = Hash(metallicRoughnessHash);
        ValidateBindings(BaseColorHash is not null, MetallicRoughnessHash is not null);
    }

    internal void ValidateBindings(bool hasBaseColor, bool hasMetallicRoughness)
    {
        if (hasBaseColor != (BaseColorHash is not null) || hasMetallicRoughness != (MetallicRoughnessHash is not null)
            || (!hasBaseColor && (!math.all(BaseColorMean == new float3(1))
                || BaseColorMetallicMean.x != BaseColorMetallicMean.y || BaseColorMetallicMean.x != BaseColorMetallicMean.z))
            || (!hasMetallicRoughness && !math.all(BaseColorMetallicMean == BaseColorMean)))
            throw new ArgumentException("Diffuse moment dependencies do not match the material texture roles.");
    }

    internal void ValidateSources(PbrTextureData? baseColor, PbrTextureData? metallicRoughness)
    {
        ValidateBindings(baseColor is not null, metallicRoughness is not null);
        if (baseColor is { Srgb: false } || metallicRoughness is { Srgb: true }
            || (baseColor is not null && BaseColorHash != baseColor.SourceLevel0Hash)
            || (metallicRoughness is not null && MetallicRoughnessHash != metallicRoughness.SourceLevel0Hash))
            throw new InvalidDataException("Diffuse moments do not match the finest texture sources.");
    }

    internal float3 Diffuse(PbrMaterialAsset material)
    {
        ValidateSources(material.BaseColor, material.MetallicRoughness);
        return material.Parameters.BaseColor * (BaseColorMean
            - System.Math.Clamp(material.Parameters.Metallic, 0, 1) * BaseColorMetallicMean);
    }
}
