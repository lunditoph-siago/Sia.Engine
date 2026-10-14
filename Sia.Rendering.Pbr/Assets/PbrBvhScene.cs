using Sia.Engine.Mesh;
using Sia.Math;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrBvhMaterialSlot(int Slot, int Material);

/// <summary>One shared local BVH and resolved material slots; no per-instance geometry.</summary>
public sealed class PbrBvhBinding
{
    private readonly PbrBvhMaterialSlot[] _materials;
    public int Geometry { get; }
    internal byte[] TransportRevision { get; }
    internal string SourceHash { get; }
    public ReadOnlySpan<PbrBvhMaterialSlot> Materials => _materials;

    internal PbrBvhBinding(int geometry, PbrBvhMaterialSlot[] materials, string sourceHash, byte[] transportRevision)
    {
        Geometry = geometry; _materials = materials; SourceHash = sourceHash; TransportRevision = transportRevision;
    }

    public int GetMaterial(int slot)
    {
        var first = 0; var last = _materials.Length - 1;
        while (first <= last) {
            var at = first + (last - first) / 2; var current = _materials[at];
            if (current.Slot == slot) return current.Material;
            if (current.Slot < slot) first = at + 1; else last = at - 1;
        }
        throw new ArgumentOutOfRangeException(nameof(slot), "BVH material slot is not bound.");
    }
}

/// <summary>
/// Transient trace input sharing acquired mesh-local BVHs. The caller retains its assets.
/// Instance acceleration, traversal, GPU upload and retirement belong to rendering consumers.
/// </summary>
public sealed partial class PbrBvhScene
{
    private readonly MeshBvhData[] _geometry;
    private readonly PbrMaterialAsset[] _materials;
    private readonly AssetId[] _materialIds;
    private readonly PbrBvhBinding[] _bindings;
    private readonly PbrMeshInstance[] _instances;
    public ReadOnlySpan<MeshBvhData> Geometry => _geometry;
    public ReadOnlySpan<PbrMaterialAsset> Materials => _materials;
    public ReadOnlySpan<AssetId> MaterialIds => _materialIds;
    public ReadOnlySpan<PbrBvhBinding> Bindings => _bindings;
    public ReadOnlySpan<PbrMeshInstance> Instances => _instances;
    public string Attribution { get; }
    public Aabb Bounds { get; }
    public ReadOnlyMemory<byte> TransportIdentity { get; }

    internal PbrBvhScene(MeshBvhData[] geometry, PbrMaterialAsset[] materials, AssetId[] materialIds,
        PbrBvhBinding[] bindings, PbrMeshInstance[] instances, string attribution, byte[] transportIdentity, CancellationToken cancellationToken)
    {
        _geometry = geometry; _materials = materials; _materialIds = materialIds; _bindings = bindings; _instances = instances; Attribution = attribution;
        TransportIdentity = transportIdentity;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        for (var i = 0; i < instances.Length; i++) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var instance = instances[i];
            var bounds = BoundsTransform.Apply(geometry[bindings[instance.Binding].Geometry].Bounds, instance.Transform);
            if (!math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)))
                throw new ArgumentException("Transformed BVH bounds must be finite.");
            minimum = math.min(minimum, bounds.Min); maximum = math.max(maximum, bounds.Max);
        }
        Bounds = instances.Length == 0 ? new(float3.zero, float3.zero) : new(minimum, maximum);
    }
}
