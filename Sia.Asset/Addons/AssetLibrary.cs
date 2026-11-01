namespace Sia.Asset;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Sia.Reactors;

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
    private readonly HashSet<Entity> _releasing = [];
    private readonly HashSet<Entity> _pendingReleases = [];
    private bool _initialized;

    private static readonly ConcurrentDictionary<Type, AssetEntry> s_assetEntries = [];

    public static void RegisterAsset<TAsset, TAssetRecord>()
        where TAsset : IAsset<TAsset, TAssetRecord>
        where TAssetRecord : class, IAssetRecord
    {
        static Entity EntityCreator(World world, IAssetRecord record, AssetLife life)
        {
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
        Listen((Entity e, in AssetMetadata.OnUnreferred _) => QueueAutomaticRelease(e));
    }

    public override void OnUninitialize(World world)
    {
        _initialized = false;
        try { base.OnUninitialize(world); }
        finally {
            _entities.Clear();
            _identifiedEntities.Clear();
            _acquiring.Clear();
            _releasing.Clear();
            _pendingReleases.Clear();
        }
    }

    private void ReleaseDependencies(in Entity entity, ref AssetMetadata meta)
    {
        if (!_releasing.Add(entity)) return;
        try {
            foreach (var referrer in meta.Referrers.ToArray()) {
                if (referrer.IsValid) referrer.Unrefer(entity);
            }
            foreach (var referred in meta.Dependents.ToArray()) {
                if (referred.IsValid) entity.Unrefer(referred);
            }
        }
        finally { _releasing.Remove(entity); }
    }

    internal bool IsReleasing(Entity entity) => _releasing.Contains(entity);

    private void QueueAutomaticRelease(Entity entity)
    {
        if (!_initialized || _releasing.Contains(entity) || !entity.IsValid
            || entity.Get<AssetMetadata>().AssetLife != AssetLife.Automatic
            || entity.Get<AssetMetadata>().Referrers.Count != 0
            || !_pendingReleases.Add(entity)) return;
        World.Dispatcher.RunAfterSend(() => {
            _pendingReleases.Remove(entity);
            if (!_initialized || !entity.IsValid || _releasing.Contains(entity)) return;
            ref var current = ref entity.Get<AssetMetadata>();
            if (current.AssetLife == AssetLife.Automatic && current.Referrers.Count == 0)
                entity.Destroy();
        });
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
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
        World.EnsureOwnerThread();
        var type = record.GetType();
        if (!s_assetEntries.TryGetValue(type, out var entry)) {
            throw new ArgumentException("Unregistered asset record type");
        }
        return entry.EntityCreator(World, record, life);
    }

    public Entity CreateEntity(
        IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
    {
        AssetMetadata.RequireAsset(World, referrer, nameof(referrer));
        var entity = CreateEntity(record, life);
        referrer.Refer(entity);
        return entity;
    }

    public Entity AcquireEntity(
        IAssetRecord record, AssetLife life = AssetLife.Automatic)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
        World.EnsureOwnerThread();
        var key = new ObjectKey<IAssetRecord>(record);
        if (!_entities.TryGetValue(key, out var entity)) {
            entity = CreateEntity(record, life);
        }
        return entity;
    }

    public Entity AcquireEntity(
        IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
    {
        AssetMetadata.RequireAsset(World, referrer, nameof(referrer));
        var entity = AcquireEntity(record, life);
        referrer.Refer(entity);
        return entity;
    }

    public bool TryGet(IAssetRecord record, out Entity entity)
        => _entities.TryGetValue(new(record), out entity);

    public Entity AcquireEntity<TAssetRecord>(AssetId id, Func<TAssetRecord> createRecord,
        AssetLife life = AssetLife.Automatic)
        where TAssetRecord : class, IAssetRecord
    {
        if (!id.IsValid) throw new ArgumentException("Asset identity must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(createRecord);
        ObjectDisposedException.ThrowIf(!_initialized || World.IsDisposed, this);
        World.EnsureOwnerThread();
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
        AssetMetadata.RequireAsset(World, referrer, nameof(referrer));
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
