using System.Runtime.CompilerServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrMaterialSlot(int Slot, MaterialReference Material);

/// <summary>One Mesh reference and its reusable slot bindings; owns no Mesh/material payload.</summary>
public sealed class PbrMeshBinding
{
    private readonly PbrMaterialSlot[] _materials;
    public MeshReference Mesh { get; }
    public ReadOnlySpan<PbrMaterialSlot> Materials => _materials;

    public PbrMeshBinding(MeshReference mesh, ReadOnlySpan<PbrMaterialSlot> materials)
    {
        if (materials.Length is < 1 or > MeshManifest.MaximumMaterialSlots) throw new ArgumentException("Invalid material-slot count.", nameof(materials));
        var copied = materials.ToArray(); Array.Sort(copied, static (a, b) => a.Slot.CompareTo(b.Slot));
        Validate(mesh, copied); Mesh = mesh; _materials = copied;
    }

    private PbrMeshBinding(MeshReference mesh, PbrMaterialSlot[] materials) { Mesh = mesh; _materials = materials; }

    internal static PbrMeshBinding Adopt(MeshReference mesh, PbrMaterialSlot[] materials)
    {
        Validate(mesh, materials); return new(mesh, materials);
    }

    private static void Validate(MeshReference mesh, ReadOnlySpan<PbrMaterialSlot> materials)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.Value.IsValid || materials.Length is < 1 or > MeshManifest.MaximumMaterialSlots)
            throw new ArgumentException("Invalid Mesh reference or material-slot count.");
        var previous = -1;
        foreach (var binding in materials) {
            if (binding.Slot <= previous || binding.Material is null || !binding.Material.Value.IsValid)
                throw new ArgumentException("Material slots must be nonnegative, unique and sorted with valid typed references.");
            previous = binding.Slot;
        }
    }

    public MaterialReference GetMaterial(int slot)
    {
        var first = 0; var last = _materials.Length - 1;
        while (first <= last) {
            var at = first + (last - first) / 2; var current = _materials[at];
            if (current.Slot == slot) return current.Material;
            if (current.Slot < slot) first = at + 1; else last = at - 1;
        }
        throw new InvalidAssetException("Mesh material slot is not bound by the scene.");
    }
}

public readonly record struct PbrMeshInstance(Guid Id, int Binding, float4x4 Transform);

/// <summary>Scene references, shared bindings and stable instances; owns no asset payload or lease.</summary>
public sealed partial class PbrSceneData
{
    public const int MaximumBindings = 4096;
    public const int MaximumInstances = 1_000_000;
    public const int MaximumMaterialReferences = 65536;
    public const int MaximumAttributionBytes = 65536;
    public const int MaximumLights = 4096;
    public const int MaximumCameras = 4096;
    private readonly PbrMeshBinding[] _bindings;
    private readonly PbrMeshInstance[] _instances;
    private readonly PbrSceneLight[]? _lights;
    private readonly PbrSceneCamera[]? _cameras;
    public ReadOnlySpan<PbrMeshBinding> Bindings => _bindings;
    public ReadOnlySpan<PbrMeshInstance> Instances => _instances;
    public bool HasAuthoredLights => _lights is not null;
    public ReadOnlySpan<PbrSceneLight> Lights => _lights;
    public bool HasAuthoredCameras => _cameras is not null;
    public ReadOnlySpan<PbrSceneCamera> Cameras => _cameras;
    public string Attribution { get; }
    public int MaterialReferenceCount { get; }
    public ulong MetadataArrayBytes => ArrayBytes(_bindings.Length, _instances.Length, MaterialReferenceCount, Lights.Length, Cameras.Length);
    public ulong EncodedBytes => 80ul + (ulong)_bindings.Length * 20 + (ulong)MaterialReferenceCount * 20
        + (ulong)_instances.Length * 84 + (ulong)Lights.Length * 76 + (ulong)Cameras.Length * 64 + (ulong)s_utf8.GetByteCount(Attribution);

    private PbrSceneData(PbrMeshBinding[] bindings, PbrMeshInstance[] instances, string attribution, PbrSceneLight[]? lights = null,
        PbrSceneCamera[]? cameras = null)
    {
        MaterialReferenceCount = Validate(bindings, instances, attribution);
        ValidateLights(lights);
        ValidateCameras(cameras);
        _bindings = bindings; _instances = instances; Attribution = attribution; _lights = lights; _cameras = cameras;
    }

    public static PbrSceneData Create(ReadOnlySpan<PbrMeshBinding> bindings, ReadOnlySpan<PbrMeshInstance> instances, string attribution = "")
    {
        if (bindings.Length > MaximumBindings || instances.Length > MaximumInstances) throw new ArgumentException("Scene metadata count limit exceeded.");
        return new(bindings.ToArray(), instances.ToArray(), attribution);
    }

    /// <summary>Snapshots explicitly authored lighting, including an empty set; existing metadata stays immutable.</summary>
    public PbrSceneData WithLights(ReadOnlySpan<PbrSceneLight> lights)
    {
        if (lights.Length > MaximumLights) throw new ArgumentException("Scene light count limit exceeded.", nameof(lights));
        return new(_bindings, _instances, Attribution, lights.ToArray(), _cameras);
    }

    /// <summary>Snapshots explicitly authored cameras, including an empty set; existing metadata stays immutable.</summary>
    public PbrSceneData WithCameras(ReadOnlySpan<PbrSceneCamera> cameras)
    {
        if (cameras.Length > MaximumCameras) throw new ArgumentException("Scene camera count limit exceeded.", nameof(cameras));
        return new(_bindings, _instances, Attribution, _lights, cameras.ToArray());
    }

    internal static ulong ArrayBytes(int bindings, int instances, int slots, int lights = 0, int cameras = 0)
        => (ulong)bindings * (ulong)IntPtr.Size + (ulong)instances * (ulong)Unsafe.SizeOf<PbrMeshInstance>()
            + (ulong)slots * (ulong)Unsafe.SizeOf<PbrMaterialSlot>() + (ulong)lights * (ulong)Unsafe.SizeOf<PbrSceneLight>()
            + (ulong)cameras * (ulong)Unsafe.SizeOf<PbrSceneCamera>();

    private static int Validate(ReadOnlySpan<PbrMeshBinding> bindings, ReadOnlySpan<PbrMeshInstance> instances, string attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        if (attribution.Length > 16384 || s_utf8.GetByteCount(attribution) > MaximumAttributionBytes
            || bindings.Length > MaximumBindings || instances.Length > MaximumInstances)
            throw new ArgumentException("Scene metadata count or attribution limit exceeded.");
        var meshes = new HashSet<AssetId>(); var materials = new HashSet<AssetId>(); var slots = 0;
        foreach (var binding in bindings) {
            ArgumentNullException.ThrowIfNull(binding); meshes.Add(binding.Mesh.Value);
            slots += binding.Materials.Length;
            if (slots > MaximumMaterialReferences) throw new ArgumentException("Scene material-reference limit exceeded.");
            foreach (var material in binding.Materials) materials.Add(material.Material.Value);
        }
        if (meshes.Overlaps(materials)) throw new ArgumentException("Mesh and material references cannot alias one logical identity.");
        var ids = new HashSet<Guid>();
        foreach (var instance in instances) {
            if (instance.Id == Guid.Empty || !ids.Add(instance.Id) || (uint)instance.Binding >= (uint)bindings.Length || !ValidTransform(instance.Transform))
                throw new ArgumentException("Instances require unique nonempty IDs, valid bindings and finite nonsingular positive affine transforms.");
        }
        return slots;
    }

    internal static bool ValidTransform(float4x4 t)
    {
        var determinant = math.determinant(t);
        return math.all(math.isfinite(t.c0)) && math.all(math.isfinite(t.c1)) && math.all(math.isfinite(t.c2)) && math.all(math.isfinite(t.c3))
            && float.IsFinite(determinant) && determinant > 1e-12f && t.c0.w == 0 && t.c1.w == 0 && t.c2.w == 0 && t.c3.w == 1;
    }
}
