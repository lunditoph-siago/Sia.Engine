using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>A section view of an acquired exact Raster page index; contains no geometry page payload.</summary>
public readonly record struct PbrRasterPagesGeometry
{
    public MeshRasterPagesData Product { get; }
    public int Section { get; }
    public ReadOnlySpan<MeshRasterPagesLod> Lods => Product.GetLods(Section);
    public Aabb Bounds => Product.Sections[Section].Bounds;

    public PbrRasterPagesGeometry(MeshRasterPagesData product, int section)
    {
        ArgumentNullException.ThrowIfNull(product);
        if ((uint)section >= (uint)product.Sections.Length) throw new ArgumentOutOfRangeException(nameof(section));
        Product = product; Section = section;
    }
}

/// <summary>Complete acquired Raster index metadata, shading and authored instances. The caller retains their asset use.</summary>
public sealed class PbrRasterPagesScene
{
    public ReadOnlyMemory<byte> TransportIdentity { get; }
    public ReadOnlyMemory<PbrRasterPagesGeometry> Geometry { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    /// <summary>Original scene actor order, expanded by original Raster section order; each array index is the stable source-instance ordinal.</summary>
    public ReadOnlyMemory<PbrSceneInstance> Instances { get; }
    public string Attribution { get; }
    public Aabb Bounds { get; }

    internal PbrRasterPagesScene(PbrRasterPagesGeometry[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances,
        string attribution, byte[] identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in geometry) {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item.Product);
            if ((uint)item.Section >= (uint)item.Product.Sections.Length)
                throw new ArgumentOutOfRangeException(nameof(geometry));
        }
        PbrSceneComposition.ValidateTables(geometry.Length, materials, instances, attribution);
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var instance in instances) {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = BoundsTransform.Apply(geometry[instance.Geometry].Bounds, instance.Transform);
            if (!math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)))
                throw new InvalidAssetException("Transformed Raster section bounds exceed finite rendering metadata.");
            minimum = math.min(minimum, bounds.Min); maximum = math.max(maximum, bounds.Max);
        }
        Geometry = geometry; Materials = materials; Instances = instances; Attribution = attribution; TransportIdentity = identity;
        Bounds = instances.Length == 0 ? new(float3.zero, float3.zero) : new(minimum, maximum);
    }
}
