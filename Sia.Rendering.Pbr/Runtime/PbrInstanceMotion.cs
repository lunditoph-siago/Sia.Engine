using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// Owns runtime instance rows. Products, local section bounds and authored bake identity stay immutable.
internal sealed class PbrInstanceMotion
{
    internal const uint DisabledBit = 0x80000000u;
    private readonly Dictionary<Guid, int[]> _rowsById;
    private readonly Aabb[] _localBounds;
    private readonly bool _stream;
    internal PbrInstanceGpu[] Rows { get; }
    internal Aabb[] WorldBounds { get; }
    internal Aabb Bounds { get; }

    internal PbrInstanceMotion(PbrRasterScene source, PbrRasterLayout layout)
    {
        Rows = layout.Instances;
        _localBounds = new Aabb[Rows.Length]; WorldBounds = new Aabb[Rows.Length];
        var ids = new Dictionary<Guid, List<int>>();
        for (var row = 0; row < Rows.Length; row++) {
            var original = source.Instances.Span[(int)Rows[row].Material.y];
            _localBounds[row] = layout.Geometry[layout.SourceGeometry[row]].Bounds;
            WorldBounds[row] = TransformBounds(_localBounds[row], original.Transform);
            if (original.AuthoredId == Guid.Empty) continue;
            if (!ids.TryGetValue(original.AuthoredId, out var rows)) ids.Add(original.AuthoredId, rows = []);
            rows.Add(row);
        }
        _rowsById = ids.ToDictionary(p => p.Key, p => p.Value.ToArray()); Bounds = Union(WorldBounds);
    }

    internal PbrInstanceMotion(PbrSceneStream source, PbrInstanceGpu[] rows)
    {
        _stream = true;
        if (rows.Length != source.Instances.Length + source.TransparentInstances.Length)
            throw new ArgumentException("Stream rows must cover every opaque and transparent instance.", nameof(rows));
        Rows = rows; _localBounds = new Aabb[rows.Length]; WorldBounds = new Aabb[rows.Length];
        var ids = new Dictionary<Guid, List<int>>();
        for (var row = 0; row < rows.Length; row++) {
            Guid id; float4x4 transform; Aabb bounds;
            if (row < source.Instances.Length) {
                var instance = source.Instances.Span[row]; var tree = source.Hierarchies[instance.AssetIndex];
                id = instance.AuthoredId; transform = instance.Transform;
                bounds = PbrSceneStream.NodeBounds(tree.Nodes[0]);
                foreach (var node in tree.Nodes.AsSpan(1, tree.Roots - 1)) bounds = Aabb.Union(bounds, PbrSceneStream.NodeBounds(node));
            }
            else {
                var instance = source.TransparentInstances[row - source.Instances.Length];
                id = instance.AuthoredId; transform = instance.Transform;
                var page = source.TransparentGeometry[instance.Geometry];
                var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
                foreach (var index in page.Page.Indices.Slice(page.FirstTriangle * 3, page.TriangleCount * 3)) {
                    var position = page.Page.GetVertex((int)index).Position; minimum = math.min(minimum, position); maximum = math.max(maximum, position);
                }
                bounds = new(minimum, maximum);
            }
            _localBounds[row] = bounds; WorldBounds[row] = TransformBounds(bounds, transform);
            if (id == Guid.Empty) continue;
            if (!ids.TryGetValue(id, out var owned)) ids.Add(id, owned = []);
            owned.Add(row);
        }
        _rowsById = ids.ToDictionary(p => p.Key, p => p.Value.ToArray()); Bounds = Union(WorldBounds);
    }

    private PbrInstanceMotion(PbrInstanceMotion source, PbrInstanceGpu[] rows, Aabb[] bounds)
    {
        _rowsById = source._rowsById; _localBounds = source._localBounds;
        _stream = source._stream;
        Rows = rows; WorldBounds = bounds; Bounds = Union(bounds);
    }

    internal PbrInstanceMotion WithTransforms(ReadOnlySpan<PbrInstanceTransform> updates)
    {
        if (updates.IsEmpty) return this;
        if (updates.Length > _rowsById.Count) throw new ArgumentException("Too many authored instance updates.", nameof(updates));
        var seen = new HashSet<Guid>();
        foreach (var update in updates) {
            if (!seen.Add(update.Id) || !_rowsById.ContainsKey(update.Id) || !PbrSceneData.ValidTransform(update.Transform))
                throw new ArgumentException("Updates require unique existing authored IDs and valid affine transforms.", nameof(updates));
            if (_stream && !float.IsFinite(PbrGpuScene.TransformNorm(update.Transform)))
                throw new ArgumentException("Instance LOD norm cannot be represented in GPU float precision.", nameof(updates));
            var normal = math.transpose(math.inverse(update.Transform));
            if (!math.all(math.isfinite(normal.c0)) || !math.all(math.isfinite(normal.c1))
                || !math.all(math.isfinite(normal.c2)) || !math.all(math.isfinite(normal.c3)))
                throw new ArgumentException("Instance normal transform cannot be represented in GPU float precision.", nameof(updates));
            foreach (var row in _rowsById[update.Id]) _ = TransformBounds(_localBounds[row], update.Transform);
        }
        var rows = (PbrInstanceGpu[])Rows.Clone(); var bounds = (Aabb[])WorldBounds.Clone();
        foreach (var update in updates) {
            var normal = math.transpose(math.inverse(update.Transform));
            foreach (var row in _rowsById[update.Id]) {
                var metadata = rows[row].Material;
                if (_stream) {
                    var norm = PbrGpuScene.TransformNorm(update.Transform);
                    metadata.y = BitConverter.SingleToUInt32Bits(norm);
                }
                rows[row] = rows[row] with { Transform = update.Transform, NormalTransform = normal, Material = metadata };
                bounds[row] = TransformBounds(_localBounds[row], update.Transform);
            }
        }
        return new(this, rows, bounds);
    }

    internal PbrInstanceMotion WithVisibility(ReadOnlySpan<PbrInstanceVisibility> updates)
    {
        if (updates.IsEmpty) return this;
        if (updates.Length > _rowsById.Count) throw new ArgumentException("Too many visibility updates.", nameof(updates));
        var seen = new HashSet<Guid>();
        foreach (var update in updates)
            if (!seen.Add(update.Id) || !_rowsById.ContainsKey(update.Id))
                throw new ArgumentException("Visibility updates require unique existing authored IDs.", nameof(updates));
        var rows = (PbrInstanceGpu[])Rows.Clone();
        foreach (var update in updates) foreach (var row in _rowsById[update.Id]) {
            var material = rows[row].Material;
            material.x = (material.x & ~DisabledBit) | (update.Enabled ? 0 : DisabledBit);
            rows[row] = rows[row] with { Material = material };
        }
        return new(this, rows, WorldBounds);
    }

    private static Aabb TransformBounds(Aabb local, float4x4 transform)
    {
        var bounds = BoundsTransform.Apply(local, transform);
        if (!math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)))
            throw new ArgumentException("Transformed section bounds must remain finite.", nameof(transform));
        return bounds;
    }

    private static Aabb Union(ReadOnlySpan<Aabb> bounds)
    {
        if (bounds.IsEmpty) return new(float3.zero, float3.zero);
        var result = bounds[0]; foreach (var item in bounds[1..]) result = Aabb.Union(result, item);
        return result;
    }
}
