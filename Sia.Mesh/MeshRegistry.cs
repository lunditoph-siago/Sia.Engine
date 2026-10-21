using Sia;
using Sia.Asset;
using Sia.Reactors;
using System.Runtime.CompilerServices;

namespace Sia.Engine.Mesh;

/// <summary>Maps already acquired Raster assets to render handles; callers own loading and building.</summary>
public sealed class MeshRegistry : ReactorBase<TypeUnion<MeshRasterAsset, AssetMetadata>>
{
    private static long s_nextHandle = -1;
    private bool _initialized;
    private readonly Dictionary<MeshHandle, Entity> _assets = [];
    private readonly Dictionary<Entity, MeshHandle> _handles = [];
    private readonly ConditionalWeakTable<MeshManifest, WeakReference<MeshRasterData>> _validatedManifests = new();

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        _initialized = true;
    }

    public override void OnUninitialize(World world)
    {
        _initialized = false;
        try { base.OnUninitialize(world); }
        finally {
            _assets.Clear();
            _handles.Clear();
            _validatedManifests.Clear();
        }
    }

    protected override void OnEntityAdded(Entity entity) { }

    protected override void OnEntityRemoved(Entity entity)
    {
        if (!_handles.Remove(entity, out var handle)) return;
        _assets.Remove(handle);
    }

    public MeshHandle Register(AssetRefer<MeshRasterRecord> reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var world = RequireWorld();
        var entity = reference.Find(world) ?? throw new AssetNotFoundException("Raster asset is not acquired.");
        if (!ReferenceEquals(entity.Host.World, world))
            throw new ArgumentException("Raster asset belongs to a different world.", nameof(reference));
        if (_handles.TryGetValue(entity, out var existing)) return existing;
        var next = Interlocked.Increment(ref s_nextHandle);
        if (next > int.MaxValue) throw new InvalidOperationException("Mesh handle space is exhausted.");
        var handle = new MeshHandle((int)next);
        _assets.Add(handle, entity);
        _handles.Add(entity, handle);
        return handle;
    }

    /// <summary>Maps the explicitly selected, already acquired Raster product of a Mesh inventory.</summary>
    public MeshHandle RegisterRaster(AssetRefer<MeshManifestRecord> reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var world = RequireWorld();
        var manifestEntity = reference.Find(world) ?? throw new AssetNotFoundException("Mesh inventory is not acquired.");
        if (!ReferenceEquals(manifestEntity.Host.World, world))
            throw new ArgumentException("Mesh inventory belongs to a different world.", nameof(reference));
        var manifest = manifestEntity.Get<MeshManifestAsset>().Data;
        var product = manifest.Raster ?? throw new InvalidAssetException("Mesh inventory has no Raster product.");
        var rasterEntity = product.Reference.Find(world) ?? throw new AssetNotFoundException("Raster product is not acquired.");
        var raster = rasterEntity.Get<MeshRasterAsset>().Data;
        if (!_validatedManifests.TryGetValue(manifest, out var cached)
            || !cached.TryGetTarget(out var previous) || !ReferenceEquals(previous, raster)) {
            ValidateRaster(manifest, raster, product.BuildKey);
            _validatedManifests.Remove(manifest);
            _validatedManifests.Add(manifest, new(raster));
        }
        return Register(new AssetRefer<MeshRasterRecord>.Entity(rasterEntity));
    }

    private static void ValidateRaster(MeshManifest manifest, MeshRasterData raster, string expectedKey)
    {
        if (raster.BuildKey != expectedKey || raster.SourceHash != manifest.SourceHash
            || !raster.Bounds.Equals(manifest.Bounds) || raster.Vertices.Length != manifest.VertexCount
            || raster.Indices.Length / 3 != manifest.TriangleCount || raster.HasBakeUvs != manifest.HasBakeUvs)
            throw new InvalidAssetException("Raster product does not match its Mesh inventory.");
        Span<bool> seen = stackalloc bool[manifest.MaterialSlots.Length];
        seen.Clear();
        var distinct = 0;
        foreach (var section in raster.Sections) {
            var index = manifest.MaterialSlots.BinarySearch(section.MaterialSlot);
            if (index < 0)
                throw new InvalidAssetException("Raster material slot is missing from its Mesh inventory.");
            if (!seen[index]) { seen[index] = true; distinct++; }
        }
        if (distinct != manifest.MaterialSlots.Length)
            throw new InvalidAssetException("Mesh inventory contains an unused Raster material slot.");
    }

    public MeshRasterData GetRaster(MeshHandle handle) => Resolve(handle).Get<MeshRasterAsset>().Data;

    public AssetRefer<MeshRasterRecord> GetReference(MeshHandle handle)
        => new AssetRefer<MeshRasterRecord>.Entity(Resolve(handle));

    public MeshData Get(MeshHandle handle) => GetRaster(handle).CopyGeometry();

    private Entity Resolve(MeshHandle handle)
    {
        RequireWorld();
        if (!handle.IsValid || !_assets.TryGetValue(handle, out var entity) || !entity.IsValid)
            throw new ArgumentException("The mesh handle is not registered or its asset has been released.", nameof(handle));
        return entity;
    }

    private World RequireWorld()
    {
        ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
        World.GetAddon<AssetLibrary>();
        return World;
    }
}
