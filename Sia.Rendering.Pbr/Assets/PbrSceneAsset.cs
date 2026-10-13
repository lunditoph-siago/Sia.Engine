using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Static material textures, coverage and optional dielectric slab transport.</summary>
/// <param name="Opacity">Surface coverage, multiplied by base-color texture alpha.</param>
/// <param name="Transmission">Dielectric transmission fraction in [0, 1]; requires AlphaBlend.</param>
/// <param name="Thickness">Slab thickness in world meters, in [0, 10]. Zero gives undistorted thin-sheet transmission.</param>
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
    /// <summary>Authored scene identity, shared by all sections of one Mesh instance.</summary>
    public Guid AuthoredId { get; init; }
    /// <summary>Mesh-local slot, independent of the resolved renderer material table.</summary>
    public int MaterialSlot { get; init; } = -1;
}

/// <summary>Transient resident rendering input for procedural callers; it has no persistence format or asset loader.</summary>
public sealed class PbrSceneAsset
{
    private static PbrTextureData?[] Maps(PbrMaterialAsset material)
        => [material.BaseColor, material.Normal, material.MetallicRoughness, material.Occlusion, material.Emissive];
    public ReadOnlyMemory<byte> TransportIdentity { get; private init; }
    public ReadOnlyMemory<MeshPatchAsset> Geometry { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    public ReadOnlyMemory<PbrSceneInstance> Instances { get; }
    public string Attribution { get; }

    private PbrSceneAsset(MeshPatchAsset[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances, string attribution)
    {
        Geometry = geometry;
        Materials = materials;
        Instances = instances;
        Attribution = attribution;
    }

    public static PbrSceneAsset Create(ReadOnlySpan<MeshPatchAsset> geometry, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution = "")
    {
        Validate(geometry, materials, instances, attribution);
        return new(geometry.ToArray(), materials.ToArray(), instances.ToArray(), attribution);
    }

    internal static PbrSceneAsset AdoptResolved(MeshPatchAsset[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances, string attribution, byte[] transportIdentity)
    {
        Validate(geometry, materials, instances, attribution);
        return new(geometry, materials, instances, attribution) { TransportIdentity = transportIdentity };
    }

    private static void Validate(ReadOnlySpan<MeshPatchAsset> geometry, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution)
    {
        foreach (var mesh in geometry) ArgumentNullException.ThrowIfNull(mesh);
        ValidateTables(geometry.Length, materials, instances, attribution);
    }

    internal static void ValidateTables(int geometryCount, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        if (attribution.Length > 16384) {
            throw new ArgumentException("PBR scene attribution is too long.", nameof(attribution));
        }
        if (geometryCount > 4096 || materials.Length > 4096 || instances.Length > 1000000) {
            throw new ArgumentException("PBR scene record counts exceed the asset format limits.");
        }
        var textures = new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance);
        foreach (var material in materials) {
            ArgumentNullException.ThrowIfNull(material);
            foreach (var texture in Maps(material)) {
                if (texture is not null) {
                    textures.Add(texture);
                }
            }
            if (!PbrMaterialValidation.ParametersValid(material.Parameters, material.NormalScale, material.OcclusionStrength,
                    material.AlphaBlend, material.Opacity, material.Transmission, material.Thickness)
                || !PbrMaterialValidation.TexturesValid(material.BaseColor, material.Normal, material.MetallicRoughness, material.Occlusion, material.Emissive)) {
                throw new ArgumentException("Invalid PBR material parameters or texture color spaces.", nameof(materials));
            }
        }
        if (textures.Count > 4096) {
            throw new ArgumentException("PBR scene texture count exceeds the asset format limit.", nameof(materials));
        }
        foreach (var instance in instances) {
            if (instance.MaterialSlot < -1 || (instance.AuthoredId == Guid.Empty) != (instance.MaterialSlot == -1))
                throw new ArgumentException("Authored instance identity and Mesh material slot must be supplied together.", nameof(instances));
            if ((uint)instance.Geometry >= (uint)geometryCount || (uint)instance.Material >= (uint)materials.Length) {
                throw new ArgumentOutOfRangeException(nameof(instances), "The scene instance references a missing geometry or material.");
            }
            if (!PbrSceneData.ValidTransform(instance.Transform)) {
                throw new ArgumentException("Scene instances require finite affine transforms with positive determinant.", nameof(instances));
            }
        }
    }
}
