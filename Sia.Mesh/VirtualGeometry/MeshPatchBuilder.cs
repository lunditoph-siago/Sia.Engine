using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    public static MeshPatchBuildResult Build(MeshData mesh, MeshPatchBuildSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var options = settings ?? MeshPatchBuildSettings.Default;
        if (!options.IsValid) {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
        var source = Prepare(mesh, cancellationToken, out var removed);
        var meshlets = MeshletBuilder.Build(source, 64, options.MaxLeafTriangles, cancellationToken: cancellationToken);
        var frontier = new List<MeshPatch>();
        var triangleOffset = 0;
        foreach (var meshlet in meshlets.Meshlets) {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = new uint[meshlet.TriangleCount * 3];
            for (var t = 0; t < meshlet.TriangleCount; t++) {
                source.Indices.AsSpan(checked((int)meshlets.SourceTriangleIndices[triangleOffset++] * 3), 3)
                    .CopyTo(indices.AsSpan(t * 3));
            }
            frontier.Add(new(Compact(source.Vertices, indices, source.BakeUvs, cancellationToken: cancellationToken), 0, []));
        }
        var roots = new List<MeshPatch>();
        var simplifications = 0;
        var targetMisses = 0;
        var unreduced = 0;
        while (frontier.Count != 0) {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<MeshPatch>();
            foreach (var group in Groups(frontier, options.MaxChildren, cancellationToken)) {
                var joined = Join(group);
                var target = System.Math.Max(1, (int)System.Math.Ceiling(joined.Indices.Length / 3d * options.ParentTriangleRatio));
                var (simplified, error) = MeshSimplifier.Reduce(joined, target, options.NormalWeight, options.UVWeight, cancellationToken);
                if (simplified.Indices.Length / 3 > target) { targetMisses++; }
                if (simplified.Indices.Length == joined.Indices.Length) {
                    unreduced++;
                    roots.AddRange(group);
                }
                else {
                    simplifications++;
                    next.Add(new(simplified, error, group));
                }
            }
            frontier = next;
        }
        return new(MeshPatchTree.Create(roots.ToArray(), 64, 124, cancellationToken),
            source.Indices.Length / 3 + removed, removed, simplifications, targetMisses, unreduced);
    }

    private static List<MeshPatch[]> Groups(List<MeshPatch> patches, int size, CancellationToken cancellationToken)
    {
        var ordered = patches.Select((patch, index) => (Patch: patch, Index: index)).ToArray();
        var groups = new List<MeshPatch[]>();
        Split(0, ordered.Length);
        return groups;

        void Split(int start, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count <= size) {
                groups.Add(ordered.AsSpan(start, count).ToArray().Select(entry => entry.Patch).ToArray());
                return;
            }
            var min = new double3(double.PositiveInfinity);
            var max = new double3(double.NegativeInfinity);
            for (var i = start; i < start + count; i++) {
                var center = Center(ordered[i].Patch.Geometry.Bounds);
                min = math.min(min, center);
                max = math.max(max, center);
            }
            var extent = max - min;
            var axis = extent.y > extent.x ? 1 : 0;
            if (extent.z > extent[axis]) { axis = 2; }
            Array.Sort(ordered, start, count, Comparer<(MeshPatch Patch, int Index)>.Create((a, b) => {
                var comparison = Center(a.Patch.Geometry.Bounds)[axis].CompareTo(Center(b.Patch.Geometry.Bounds)[axis]);
                return comparison != 0 ? comparison : a.Index.CompareTo(b.Index);
            }));
            var half = count / 2;
            Split(start, half);
            Split(start + half, count - half);
        }
    }

    private static MeshData Join(MeshPatch[] patches)
    {
        var vertices = new List<MeshVertex>();
        var bakeUvs = new List<float2>();
        var indices = new List<uint>();
        var identities = new Dictionary<MeshVertexIdentity, uint>();
        foreach (var patch in patches) {
            foreach (var index in patch.Geometry.Indices) {
                var vertex = patch.Geometry.Vertices[index];
                var bakeUv = patch.Geometry.HasBakeUvs ? patch.Geometry.BakeUvs[index] : default;
                var identity = MeshVertexIdentity.Create(vertex, bakeUv);
                if (!identities.TryGetValue(identity, out var mapped)) {
                    identities.Add(identity, mapped = (uint)vertices.Count);
                    vertices.Add(vertex);
                    if (patch.Geometry.HasBakeUvs) bakeUvs.Add(bakeUv);
                }
                indices.Add(mapped);
            }
        }
        return WithBounds(vertices.ToArray(), indices.ToArray(), bakeUvs.ToArray());
    }

    internal static MeshData Compact(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<float2> bakeUvs = default, ulong maximumBytes = ulong.MaxValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indexBytes = (ulong)indices.Length * sizeof(uint);
        if (indexBytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var output = new List<MeshVertex>();
        var outputBakeUvs = new List<float2>();
        var remap = new Dictionary<uint, uint>();
        var compact = new uint[indices.Length];
        for (var i = 0; i < indices.Length; i++) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!remap.TryGetValue(indices[i], out var mapped)) {
                if (indexBytes + ((ulong)output.Count + 1) * (ulong)(MeshVertex.Stride + (bakeUvs.IsEmpty ? 0 : 8)) > maximumBytes)
                    throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Compacted Mesh input exceeds its array budget.");
                remap.Add(indices[i], mapped = (uint)output.Count);
                output.Add(vertices[(int)indices[i]]);
                if (!bakeUvs.IsEmpty) outputBakeUvs.Add(bakeUvs[(int)indices[i]]);
            }
            compact[i] = mapped;
        }
        return WithBounds(output.ToArray(), compact, outputBakeUvs.ToArray());
    }

    private static MeshData WithBounds(MeshVertex[] vertices, uint[] indices, float2[] bakeUvs)
    {
        var min = new float3(float.PositiveInfinity);
        var max = new float3(float.NegativeInfinity);
        foreach (var vertex in vertices) { min = math.min(min, vertex.Position); max = math.max(max, vertex.Position); }
        return new(vertices, indices, vertices.Length == 0 ? default : new(min, max)) { BakeUvs = bakeUvs };
    }

    private static double3 Position(MeshVertex vertex) => new(vertex.Position.x, vertex.Position.y, vertex.Position.z);
    private static double3 Center(Aabb bounds) => (new double3(bounds.Min.x, bounds.Min.y, bounds.Min.z)
        + new double3(bounds.Max.x, bounds.Max.y, bounds.Max.z)) * 0.5;
}
