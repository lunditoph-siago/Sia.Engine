using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Mesh-local section view of an acquired Virtual index; contains no page payload or instances.</summary>
public readonly record struct PbrVirtualGeometry
{
    public MeshVirtualPagesData Product { get; }
    public int Part { get; }
    public PbrVirtualGeometry(MeshVirtualPagesData product, int part)
    {
        ArgumentNullException.ThrowIfNull(product);
        if ((uint)part >= product.Parts.Length) throw new ArgumentOutOfRangeException(nameof(part));
        Product = product; Part = part;
    }
}

/// <summary>Transient composition of acquired indexes, shading and instances. The caller retains their asset use.</summary>
public sealed class PbrVirtualScene
{
    public ReadOnlyMemory<byte> TransportIdentity { get; }
    public ReadOnlyMemory<PbrVirtualGeometry> Geometry { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    public ReadOnlyMemory<PbrSceneInstance> Instances { get; }
    public string Attribution { get; }
    public Aabb Bounds { get; }
    internal PbrVirtualScene(PbrVirtualGeometry[] geometry, PbrMaterialAsset[] materials, PbrSceneInstance[] instances,
        string attribution, byte[] identity)
    {
        PbrSceneComposition.ValidateTables(geometry.Length, materials, instances, attribution);
        Geometry = geometry; Materials = materials; Instances = instances; Attribution = attribution; TransportIdentity = identity;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var instance in instances) {
            var item = geometry[instance.Geometry]; var part = item.Product.Parts[item.Part];
            foreach (var node in item.Product.Nodes.Slice(part.FirstNode, part.RootCount)) {
                var bounds = BoundsTransform.Apply(node.Bounds, instance.Transform);
                minimum = math.min(minimum, bounds.Min); maximum = math.max(maximum, bounds.Max);
            }
        }
        Bounds = !math.all(math.isfinite(minimum)) ? new(float3.zero, float3.zero) : new(minimum, maximum);
    }
}
