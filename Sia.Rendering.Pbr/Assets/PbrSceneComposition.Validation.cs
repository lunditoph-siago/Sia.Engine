namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneComposition
{
    private static PbrTextureData?[] Maps(PbrMaterialAsset material)
        => [material.BaseColor, material.Normal, material.MetallicRoughness, material.Occlusion, material.Emissive];
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
