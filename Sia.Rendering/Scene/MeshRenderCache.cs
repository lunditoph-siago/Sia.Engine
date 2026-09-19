using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

public readonly record struct MeshSceneInstance<TMaterial>(float4x4 Transform, float4x4 Normal, TMaterial Material);

/// <summary>Material-specific extraction over shared mesh identity, bounds and change tracking.</summary>
public abstract class MeshRenderCache<TMaterial, TInstance> : IAddon
    where TMaterial : struct, IEquatable<TMaterial>
    where TInstance : unmanaged, IEquatable<TInstance>
{
    private static readonly IEntityMatcher s_Matcher = Matchers.Of<
        global::Sia.Engine.Mesh.Mesh, TMaterial, MeshRenderer, GlobalTransform, WorldBounds>();
    private World _world = null!;
    private readonly List<Entity> _entities = [];
    private readonly Dictionary<Entity, Cached> _cache = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<MeshHandle> _meshes = [];
    private readonly List<Aabb> _bounds = [];
    private readonly List<TInstance> _data = [];
    private readonly List<int> _visible = [];
    private Entity[] _owners = [];
    private MeshHandle[] _acceptedMeshes = [];
    private Aabb[] _acceptedBounds = [];
    private Aabb[] _batches = [];
    private const int BatchSize = 128;

    public ReadOnlyMemory<TInstance> Data { get; private set; } = ReadOnlyMemory<TInstance>.Empty;
    public IReadOnlyList<MeshHandle> MeshHandles => _acceptedMeshes;
    public Aabb? ShadowBounds { get; private set; }
    public ulong Version { get; private set; }

    void IAddon.OnInitialize(World world) => _world = world;
    protected abstract TInstance Convert(in MeshSceneInstance<TMaterial> instance);

    public void Refresh()
    {
        _entities.Clear(); _meshes.Clear(); _bounds.Clear(); _data.Clear(); _seen.Clear();
        _world.Query(s_Matcher, _entities, static (in List<Entity> items, Entity entity) => items.Add(entity));
        foreach (var entity in _entities) {
            _seen.Add(entity);
            var mesh = entity.Get<global::Sia.Engine.Mesh.Mesh>().Handle;
            var transform = entity.Get<GlobalTransform>().Affine;
            var bounds = entity.Get<WorldBounds>().World;
            var material = entity.Get<TMaterial>();
            var exists = _cache.TryGetValue(entity, out var previous);
            float4x4 matrix = transform;
            if (!exists || !previous.Source.Transform.Equals(matrix) || !previous.Source.Material.Equals(material)) {
                var normal = exists && previous.Source.Transform.Equals(matrix) ? previous.Source.Normal
                    : new float4x4(math.transpose(math.inverse(transform.RotationScale)), float3.zero);
                var source = new MeshSceneInstance<TMaterial>(matrix, normal, material);
                previous = new(source, Convert(in source));
                _cache[entity] = previous;
            }
            _meshes.Add(mesh); _bounds.Add(bounds); _data.Add(previous.Instance);
        }
        // Removal must not retain ECS owners; snapshots stay immutable for the current consumer.
        foreach (var owner in _owners) { if (!_seen.Contains(owner)) _cache.Remove(owner); }
        if (CollectionsMarshal.AsSpan(_entities).SequenceEqual(_owners)
            && CollectionsMarshal.AsSpan(_meshes).SequenceEqual(_acceptedMeshes)
            && CollectionsMarshal.AsSpan(_bounds).SequenceEqual(_acceptedBounds)
            && CollectionsMarshal.AsSpan(_data).SequenceEqual(Data.Span)) return;
        _owners = _entities.ToArray(); _acceptedMeshes = _meshes.ToArray();
        _acceptedBounds = _bounds.ToArray(); Data = _data.ToArray(); Version++;
        ShadowBounds = null;
        _batches = new Aabb[(_bounds.Count + BatchSize - 1) / BatchSize];
        for (var batch = 0; batch < _batches.Length; batch++) {
            var start = batch * BatchSize;
            var bounds = _bounds[start];
            for (var i = start + 1; i < System.Math.Min(start + BatchSize, _bounds.Count); i++) bounds = Aabb.Union(bounds, _bounds[i]);
            _batches[batch] = bounds;
            ShadowBounds = ShadowBounds is { } previous ? Aabb.Union(previous, bounds) : bounds;
        }
    }

    public IReadOnlyList<int> Cull(Frustum frustum)
    {
        _visible.Clear();
        if (_batches.Length == 0) return _visible;
        var culler = new FrustumCuller(in frustum);
        for (var batch = 0; batch < _batches.Length; batch++) {
            if (!culler.Intersects(_batches[batch])) continue;
            var start = batch * BatchSize;
            for (var i = start; i < System.Math.Min(start + BatchSize, _acceptedBounds.Length); i++) {
                if (culler.Intersects(_acceptedBounds[i])) _visible.Add(i);
            }
        }
        return _visible;
    }

    private readonly record struct Cached(MeshSceneInstance<TMaterial> Source, TInstance Instance);
}
