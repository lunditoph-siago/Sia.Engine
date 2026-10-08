namespace Sia.Asset;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Sia.Reactors;

/// <summary>
/// Generic ECS asset identity, references and lifetime. Callers compose domain loading,
/// cooking and publication workflows through the asset contracts.
/// </summary>
public class AssetLibrary : ReactorBase<TypeUnion<AssetMetadata>>
{
    private record struct AssetEntry(
        Func<World, IAssetRecord, AssetLife, Entity> EntityCreator);

    public IReadOnlyDictionary<ObjectKey<IAssetRecord>, Entity> Entities => _entities;

    public Entity this[IAssetRecord record]
        => _entities.TryGetValue(new(record), out var entity)
            ? entity : throw new KeyNotFoundException("Asset entity not found");

    private readonly Dictionary<ObjectKey<IAssetRecord>, Entity> _entities = [];
    private readonly Dictionary<AssetId, Entity> _identifiedEntities = [];
    private readonly HashSet<AssetId> _acquiring = [];
    private bool _initialized;

    private static readonly ConcurrentDictionary<Type, AssetEntry> s_assetEntries = [];

    public static void RegisterAsset<TAsset, TAssetRecord>()
        where TAsset : IAsset<TAsset, TAssetRecord>
        where TAssetRecord : class, IAssetRecord
    {
        static Entity EntityCreator(World world, IAssetRecord record, AssetLife life)
        {
            // Construct before allocating an entity so a failed constructor leaves no orphan.
            var bundle = TAsset.Create(Unsafe.As<TAssetRecord>(record), life);
            return world.Create().AddBundle(bundle);
        }

        s_assetEntries.AddOrUpdate(typeof(TAssetRecord),
            t => new(EntityCreator), (t, e) => new(EntityCreator));
    }

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        _initialized = true;

        Listen((Entity e, in WorldEvents.Remove cmd) => {
            ref var meta = ref e.GetOrNullRef<AssetMetadata>();
            if (Unsafe.IsNullRef(ref meta)) return;
            ReleaseDependencies(e, ref meta);
        });
    }

    public override void OnUninitialize(World world)
    {
        _initialized = false;
        try { base.OnUninitialize(world); }
        finally {
            _entities.Clear();
            _identifiedEntities.Clear();
            _acquiring.Clear();
        }
    }

    private void ReleaseDependencies(in Entity entity, ref AssetMetadata meta)
    {
        foreach (var referrer in meta.Referrers.ToArray()) {
            if (referrer.IsValid) referrer.Unrefer(entity);
        }
        foreach (var referred in meta.Dependents.ToArray()) {
            if (!referred.IsValid) continue;
            entity.Unrefer(referred);

            ref var refereeMeta = ref referred.Get<AssetMetadata>();
            if (refereeMeta.AssetLife == AssetLife.Automatic
                    && refereeMeta.Referrers.Count == 0) {
                World.Dispatcher.RunAfterSend(() => {
                    if (!referred.IsValid) return;
                    ref var current = ref referred.Get<AssetMetadata>();
                    if (current.AssetLife == AssetLife.Automatic && current.Referrers.Count == 0)
                        referred.Destroy();
                });
            }
        }
    }

    protected override void OnEntityAdded(Entity entity)
    {
        ref var metadata = ref entity.Get<AssetMetadata>();
        var assetRecord = metadata.AssetSource;
        if (assetRecord != null) {
            _entities.Add(new(assetRecord), entity);
        }
        if (metadata.Id is { } id) {
            _identifiedEntities.Add(id, entity);
        }
    }

    protected override void OnEntityRemoved(Entity entity)
    {
        ref var metadata = ref entity.Get<AssetMetadata>();
        var assetRecord = metadata.AssetSource;
        if (assetRecord != null) {
            _entities.Remove(new(assetRecord));
        }
        if (metadata.Id is { } id) {
            _identifiedEntities.Remove(id);
        }
    }

    public Entity CreateEntity(
        IAssetRecord record, AssetLife life = AssetLife.Automatic)
    {
        var type = record.GetType();
        if (!s_assetEntries.TryGetValue(type, out var entry)) {
            throw new ArgumentException("Unregistered asset record type");
        }
        return entry.EntityCreator(World, record, life);
    }

    public Entity CreateEntity(
        IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
    {
        var entity = CreateEntity(record, life);
        referrer.Refer(entity);
        return entity;
    }

    public Entity AcquireEntity(
        IAssetRecord record, AssetLife life = AssetLife.Automatic)
    {
        var key = new ObjectKey<IAssetRecord>(record);
        if (!_entities.TryGetValue(key, out var entity)) {
            entity = CreateEntity(record, life);
        }
        return entity;
    }

    public Entity AcquireEntity(
        IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
    {
        var entity = AcquireEntity(record, life);
        referrer.Refer(entity);
        return entity;
    }

    public bool TryGet(IAssetRecord record, out Entity entity)
        => _entities.TryGetValue(new(record), out entity);

    /// <summary>
    /// Acquires a logical asset through the existing record/entity path. The factory runs
    /// synchronously only on a cache miss and must return the registered concrete record type.
    /// The caller prepares domain records and chooses the assets and dependencies to acquire.
    /// Call on the world's owner context; asynchronous IO must complete before this operation.
    /// </summary>
    public Entity AcquireEntity<TAssetRecord>(AssetId id, Func<TAssetRecord> createRecord,
        AssetLife life = AssetLife.Automatic)
        where TAssetRecord : class, IAssetRecord
    {
        if (!id.IsValid) throw new ArgumentException("Asset identity must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(createRecord);
        ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
        if (_identifiedEntities.TryGetValue(id, out var existing)) {
            if (!MatchesRecord<TAssetRecord>(existing))
                throw new InvalidAssetException("Asset identity is already assigned to a different record type.");
            return existing;
        }
        if (!_acquiring.Add(id))
            throw new InvalidAssetException("Recursive acquisition of the same asset identity.");
        try {
            var record = createRecord() ?? throw new InvalidAssetException("Asset factory returned no record.");
            if (record.GetType() != typeof(TAssetRecord))
                throw new InvalidAssetException("Asset factory must return its registered concrete record type.");
            ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
            var entity = AcquireEntity(record, life);
            ref var metadata = ref entity.Get<AssetMetadata>();
            if (metadata.Id is { } assigned && assigned != id)
                throw new InvalidAssetException("Asset record is already assigned to another identity.");
            _identifiedEntities.Add(id, entity);
            metadata.Id = id;
            return entity;
        }
        finally { _acquiring.Remove(id); }
    }

    public Entity AcquireEntity<TAssetRecord>(AssetId id, Func<TAssetRecord> createRecord,
        Entity referrer, AssetLife life = AssetLife.Automatic)
        where TAssetRecord : class, IAssetRecord
    {
        if (!referrer.IsValid || !ReferenceEquals(referrer.Host.World, World)
            || Unsafe.IsNullRef(ref referrer.GetOrNullRef<AssetMetadata>()))
            throw new ArgumentException("Referrer must be an asset entity in this world.", nameof(referrer));
        var entity = AcquireEntity(id, createRecord, life);
        referrer.Refer(entity);
        return entity;
    }

    public bool TryGet<TAssetRecord>(AssetId id, out Entity entity)
        where TAssetRecord : IAssetRecord
    {
        if (_initialized && _identifiedEntities.TryGetValue(id, out var found) && MatchesRecord<TAssetRecord>(found)) {
            entity = found;
            return true;
        }
        entity = default;
        return false;
    }

    private static bool MatchesRecord<TAssetRecord>(Entity entity)
        where TAssetRecord : IAssetRecord
        => entity.IsValid && entity.Get<AssetMetadata>().AssetType.IsAssignableTo(typeof(IAsset<TAssetRecord>));
}
