using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>A section view of an acquired Raster product, without copying or cooking geometry.</summary>
public sealed class PbrRasterGeometry
{
    public MeshRasterData Product { get; }
    public int Section { get; }
    public ReadOnlySpan<uint> Indices => Product.Indices.Slice(Product.Sections[Section].FirstIndex, Product.Sections[Section].IndexCount);
    public Aabb Bounds { get; }

    public PbrRasterGeometry(MeshRasterData product, int section)
    {
        ArgumentNullException.ThrowIfNull(product);
        if ((uint)section >= (uint)product.Sections.Length) throw new ArgumentOutOfRangeException(nameof(section));
        Product = product; Section = section;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var index in Indices) {
            var p = product.Vertices[(int)index].Position;
            minimum = math.min(minimum, p); maximum = math.max(maximum, p);
        }
        Bounds = new(minimum, maximum);
    }
}

/// <summary>Transient conventional render input. The caller retains its selected assets while in use.</summary>
public sealed class PbrRasterScene
{
    public ReadOnlyMemory<byte> TransportIdentity { get; private init; }
    public ReadOnlyMemory<PbrRasterGeometry> Geometry { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    public ReadOnlyMemory<PbrSceneInstance> Instances { get; }
    public string Attribution { get; }
    public Aabb Bounds { get; }

    private PbrRasterScene(PbrRasterGeometry[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances, string attribution)
    {
        Geometry = geometry; Materials = materials; Instances = instances; Attribution = attribution;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var instance in instances) {
            var bounds = BoundsTransform.Apply(geometry[instance.Geometry].Bounds, instance.Transform);
            minimum = math.min(minimum, bounds.Min); maximum = math.max(maximum, bounds.Max);
        }
        Bounds = instances.Length == 0 ? new(float3.zero, float3.zero) : new(minimum, maximum);
    }

    public static PbrRasterScene Create(ReadOnlySpan<PbrRasterGeometry> geometry, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution = "")
    {
        Validate(geometry, materials, instances, attribution);
        return new(geometry.ToArray(), materials.ToArray(), instances.ToArray(), attribution);
    }

    internal static PbrRasterScene AdoptResolved(PbrRasterGeometry[] geometry, PbrMaterialAsset[] materials,
        PbrSceneInstance[] instances, string attribution, byte[] transportIdentity)
    {
        Validate(geometry, materials, instances, attribution);
        return new(geometry, materials, instances, attribution) { TransportIdentity = transportIdentity };
    }

    private static void Validate(ReadOnlySpan<PbrRasterGeometry> geometry, ReadOnlySpan<PbrMaterialAsset> materials,
        ReadOnlySpan<PbrSceneInstance> instances, string attribution)
    {
        foreach (var item in geometry) ArgumentNullException.ThrowIfNull(item);
        PbrSceneAsset.ValidateTables(geometry.Length, materials, instances, attribution);
    }
}
