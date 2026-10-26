using Sia.Asset;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Material parameters and logical texture references, without texture payload ownership.</summary>
public sealed partial class PbrMaterialData
{
    public PbrMaterial Parameters { get; }
    public TextureReference? BaseColor { get; }
    public TextureReference? Normal { get; }
    public TextureReference? MetallicRoughness { get; }
    public TextureReference? Occlusion { get; }
    public TextureReference? Emissive { get; }
    public float NormalScale { get; }
    public float OcclusionStrength { get; }
    public bool DoubleSided { get; }
    public bool AlphaBlend { get; }
    public float Opacity { get; }
    public float Transmission { get; }
    public float Thickness { get; }
    public PbrDiffuseMoments? DiffuseMoments { get; }

    public IEnumerable<TextureReference> TextureReferences {
        get {
            if (BaseColor is not null) yield return BaseColor;
            if (Normal is not null) yield return Normal;
            if (MetallicRoughness is not null) yield return MetallicRoughness;
            if (Occlusion is not null) yield return Occlusion;
            if (Emissive is not null) yield return Emissive;
        }
    }

    public PbrMaterialData(PbrMaterial parameters, TextureReference? baseColor = null, TextureReference? normal = null,
        TextureReference? metallicRoughness = null, TextureReference? occlusion = null, TextureReference? emissive = null,
        float normalScale = 1, float occlusionStrength = 1, bool doubleSided = false, bool alphaBlend = false,
        float opacity = 1, float transmission = 0, float thickness = 0, PbrDiffuseMoments? diffuseMoments = null)
    {
        if (!PbrMaterialValidation.ParametersValid(parameters, normalScale, occlusionStrength, alphaBlend, opacity, transmission, thickness))
            throw new ArgumentException("Invalid PBR material parameters.");
        BaseColor = baseColor; Normal = normal; MetallicRoughness = metallicRoughness; Occlusion = occlusion; Emissive = emissive;
        foreach (var reference in TextureReferences)
            if (!reference.Value.IsValid) throw new ArgumentException("Texture reference identity must be nonempty.");
        if (Conflicts(baseColor, normal) || Conflicts(baseColor, metallicRoughness) || Conflicts(baseColor, occlusion)
            || Conflicts(emissive, normal) || Conflicts(emissive, metallicRoughness) || Conflicts(emissive, occlusion))
            throw new ArgumentException("One texture identity cannot require both linear and sRGB decoding.");
        diffuseMoments?.ValidateBindings(baseColor is not null, metallicRoughness is not null);
        Parameters = parameters; NormalScale = normalScale; OcclusionStrength = occlusionStrength;
        DoubleSided = doubleSided; AlphaBlend = alphaBlend; Opacity = opacity; Transmission = transmission; Thickness = thickness;
        DiffuseMoments = diffuseMoments;
    }

    private static bool Conflicts(TextureReference? color, TextureReference? linear) => color is not null && color == linear;

    /// <summary>Forms a transient shading value using caller lookup of already acquired textures; does not load or pin.</summary>
    public PbrMaterialAsset ResolveTextures(Func<TextureReference, PbrTextureData> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        PbrTextureData? Read(TextureReference? reference) => reference is null ? null
            : lookup(reference) ?? throw new AssetNotFoundException("Material texture lookup returned no data.");
        var baseColor = Read(BaseColor); var normal = Read(Normal); var metallicRoughness = Read(MetallicRoughness);
        var occlusion = Read(Occlusion); var emissive = Read(Emissive);
        if (!PbrMaterialValidation.TexturesValid(baseColor, normal, metallicRoughness, occlusion, emissive))
            throw new InvalidAssetException("Material texture color space does not match its role.");
        DiffuseMoments?.ValidateSources(baseColor, metallicRoughness);
        return new(Parameters, baseColor, normal, metallicRoughness, occlusion, emissive, NormalScale, OcclusionStrength,
            DoubleSided, AlphaBlend, Opacity, Transmission, Thickness, DiffuseMoments);
    }
}
