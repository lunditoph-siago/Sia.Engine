using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrMaterialAsset(PbrMaterial Parameters, PbrTextureData? BaseColor = null,
    PbrTextureData? Normal = null, PbrTextureData? MetallicRoughness = null, PbrTextureData? Occlusion = null,
    PbrTextureData? Emissive = null, float NormalScale = 1, float OcclusionStrength = 1,
    bool DoubleSided = false, bool AlphaBlend = false, float Opacity = 1);

public readonly record struct PbrSceneInstance(int Geometry, int Material, float4x4 Transform);

public sealed partial class PbrSceneAsset
{
    public const int FormatVersion = 2;
    public ReadOnlyMemory<MeshPatchAsset> Geometry { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    public ReadOnlyMemory<PbrSceneInstance> Instances { get; }
    public string Attribution { get; }

    private PbrSceneAsset(MeshPatchAsset[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances, string attribution)
    {
        Geometry = geometry; Materials = materials; Instances = instances; Attribution = attribution;
    }

    public static PbrSceneAsset Create(ReadOnlySpan<MeshPatchAsset> geometry, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution = "")
    {
        ArgumentNullException.ThrowIfNull(attribution);
        if (attribution.Length > 16384) { throw new ArgumentException("PBR scene attribution is too long.", nameof(attribution)); }
        if (geometry.Length > 4096 || materials.Length > 4096 || instances.Length > 1000000) {
            throw new ArgumentException("PBR scene record counts exceed the asset format limits.");
        }
        var textures = new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance);
        foreach (var mesh in geometry) { ArgumentNullException.ThrowIfNull(mesh); }
        foreach (var material in materials) {
            ArgumentNullException.ThrowIfNull(material);
            foreach (var texture in Maps(material)) { if (texture is not null) { textures.Add(texture); } }
            var p = material.Parameters;
            if (!Finite(p.BaseColor) || !Finite(p.EmissiveColor) || !float.IsFinite(p.EmissiveStrength) || p.EmissiveStrength < 0
                || !float.IsFinite(p.Metallic) || p.Metallic is < 0 or > 1 || !float.IsFinite(p.Roughness) || p.Roughness is < 0 or > 1
                || !float.IsFinite(material.NormalScale)
                || !float.IsFinite(material.Opacity) || material.Opacity is < 0 or > 1
                || !float.IsFinite(material.OcclusionStrength) || material.OcclusionStrength is < 0 or > 1
                || material.BaseColor is { Srgb: false } || material.Emissive is { Srgb: false }
                || material.Normal is { Srgb: true } || material.MetallicRoughness is { Srgb: true } || material.Occlusion is { Srgb: true }) {
                throw new ArgumentException("Invalid PBR material parameters or texture color spaces.", nameof(materials));
            }
        }
        if (textures.Count > 4096) { throw new ArgumentException("PBR scene texture count exceeds the asset format limit.", nameof(materials)); }
        foreach (var instance in instances) {
            if ((uint)instance.Geometry >= (uint)geometry.Length || (uint)instance.Material >= (uint)materials.Length) {
                throw new ArgumentOutOfRangeException(nameof(instances), "The scene instance references a missing geometry or material.");
            }
            var t = instance.Transform;
            var determinant = math.determinant(t);
            if (!Finite(t.c0) || !Finite(t.c1) || !Finite(t.c2) || !Finite(t.c3) || !float.IsFinite(determinant) || determinant <= 1e-12f
                || t.c0.w != 0 || t.c1.w != 0 || t.c2.w != 0 || t.c3.w != 1) {
                throw new ArgumentException("Scene instances require finite affine transforms with positive determinant.", nameof(instances));
            }
        }
        return new(geometry.ToArray(), materials.ToArray(), instances.ToArray(), attribution);
    }

    private static bool Finite(float3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(float4 value) => Finite(value.xyz) && float.IsFinite(value.w);
}
