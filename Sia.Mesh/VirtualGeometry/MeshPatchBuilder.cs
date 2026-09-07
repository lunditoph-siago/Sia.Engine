using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    public static MeshPatchBuildResult Build(MeshData mesh, MeshPatchBuildSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var options = settings ?? MeshPatchBuildSettings.Default;
        if (options.MaxLeafTriangles is < 1 or > 512 || options.MaxChildren is < 2 or > 8
            || !float.IsFinite(options.ParentTriangleRatio) || options.ParentTriangleRatio <= 0 || options.ParentTriangleRatio >= 1
            || !float.IsFinite(options.NormalWeight) || options.NormalWeight < 0
            || !float.IsFinite(options.UVWeight) || options.UVWeight < 0) {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
        var source = Prepare(mesh, cancellationToken, out var removed);
        var meshlets = MeshletBuilder.Build(source, 64, options.MaxLeafTriangles, cancellationToken);
        var frontier = new List<MeshPatch>();
        var triangleOffset = 0;
        foreach (var meshlet in meshlets.Meshlets) {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = new uint[meshlet.TriangleCount * 3];
            for (var t = 0; t < meshlet.TriangleCount; t++) {
                source.Indices.AsSpan(checked((int)meshlets.SourceTriangleIndices[triangleOffset++] * 3), 3)
                    .CopyTo(indices.AsSpan(t * 3));
            }
            frontier.Add(new(Compact(source.Vertices, indices), 0, []));
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
                var (simplified, error) = Simplify(joined, target, options, cancellationToken);
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
        return new(MeshPatchTree.Create(roots.ToArray(), cancellationToken: cancellationToken),
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
        var indices = new List<uint>();
        var identities = new Dictionary<VertexIdentity, uint>();
        foreach (var patch in patches) {
            foreach (var index in patch.Geometry.Indices) {
                var vertex = patch.Geometry.Vertices[index];
                var identity = Identity(vertex);
                if (!identities.TryGetValue(identity, out var mapped)) {
                    identities.Add(identity, mapped = (uint)vertices.Count);
                    vertices.Add(vertex);
                }
                indices.Add(mapped);
            }
        }
        return WithBounds(vertices.ToArray(), indices.ToArray());
    }

    private static MeshData Compact(MeshVertex[] vertices, ReadOnlySpan<uint> indices)
    {
        var output = new List<MeshVertex>();
        var remap = new Dictionary<uint, uint>();
        var compact = new uint[indices.Length];
        for (var i = 0; i < indices.Length; i++) {
            if (!remap.TryGetValue(indices[i], out var mapped)) {
                remap.Add(indices[i], mapped = (uint)output.Count);
                output.Add(vertices[indices[i]]);
            }
            compact[i] = mapped;
        }
        return WithBounds(output.ToArray(), compact);
    }

    private static MeshData WithBounds(MeshVertex[] vertices, uint[] indices)
    {
        var min = new float3(float.PositiveInfinity);
        var max = new float3(float.NegativeInfinity);
        foreach (var vertex in vertices) { min = math.min(min, vertex.Position); max = math.max(max, vertex.Position); }
        return new(vertices, indices, vertices.Length == 0 ? default : new(min, max));
    }

    private static double3 Position(MeshVertex vertex) => new(vertex.Position.x, vertex.Position.y, vertex.Position.z);
    private static double3 Center(Aabb bounds) => (new double3(bounds.Min.x, bounds.Min.y, bounds.Min.z)
        + new double3(bounds.Max.x, bounds.Max.y, bounds.Max.z)) * 0.5;
    private static VertexIdentity Identity(MeshVertex v) => new(v.Position.x, v.Position.y, v.Position.z,
        v.Normal.x, v.Normal.y, v.Normal.z, v.UV.x, v.UV.y, v.Tangent.x, v.Tangent.y, v.Tangent.z, v.Tangent.w);
    private readonly record struct VertexIdentity(float X, float Y, float Z, float Nx, float Ny, float Nz, float U, float V,
        float Tx, float Ty, float Tz, float Tw);
}
