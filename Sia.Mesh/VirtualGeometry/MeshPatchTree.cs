using System.Buffers;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchTree
{
    private readonly MeshData _geometry;
    private readonly MeshletData _meshlets;

    public ReadOnlyMemory<MeshPatchNode> Nodes { get; }
    public int RootCount { get; }
    public int FinestTriangleCount { get; }

    private MeshPatchTree(MeshPatchNode[] nodes, int roots, int finestTriangles, MeshData geometry, MeshletData meshlets)
    {
        Nodes = nodes;
        RootCount = roots;
        FinestTriangleCount = finestTriangles;
        _geometry = geometry;
        _meshlets = meshlets;
    }

    public (MeshData Geometry, MeshletData Meshlets) CopyGeometry() => (
        new(_geometry.Vertices.ToArray(), _geometry.Indices.ToArray(), _geometry.Bounds),
        new(_meshlets.Meshlets.ToArray(), _meshlets.VertexIndices.ToArray(),
            _meshlets.TriangleIndices.ToArray(), _meshlets.SourceTriangleIndices.ToArray()));

    public static MeshPatchTree Create(ReadOnlySpan<MeshPatch> roots, int maxVertices = 64, int maxTriangles = 124,
        CancellationToken cancellationToken = default)
    {
        var patches = new List<(MeshPatch Patch, int Parent)>();
        var seen = new HashSet<MeshPatch>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots) { Add(root, -1); }
        var snapshots = new List<BuildPatch>();
        for (var i = 0; i < patches.Count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var (patch, parent) = patches[i];
            ArgumentNullException.ThrowIfNull(patch.Geometry);
            ArgumentNullException.ThrowIfNull(patch.Geometry.Vertices);
            ArgumentNullException.ThrowIfNull(patch.Geometry.Indices);
            ArgumentNullException.ThrowIfNull(patch.Children);
            var children = patch.Children.ToArray();
            if (!float.IsFinite(patch.LocalEstimatedSpatialError) || patch.LocalEstimatedSpatialError < 0
                || (children.Length == 0 && patch.LocalEstimatedSpatialError != 0)
                || patch.Geometry.Indices.Length == 0) {
                throw new ArgumentException("Patches require nonempty geometry, finite nonnegative local error, and zero leaf error.", nameof(roots));
            }
            var childOffset = patches.Count;
            foreach (var child in children) { Add(child, i); }
            snapshots.Add(new(new(patch.Geometry.Vertices.ToArray(), patch.Geometry.Indices.ToArray(), default),
                patch.LocalEstimatedSpatialError, parent, childOffset, children.Length));
        }

        var nodes = new MeshPatchNode[snapshots.Count];
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var clusters = new List<Meshlet>();
        var vertexReferences = new List<uint>();
        var localIndices = new List<byte>();
        var sourceTriangles = new List<uint>();
        var vertexIdentities = new Dictionary<VertexIdentity, int>();
        var boundaries = new Dictionary<(int, int), int>[nodes.Length];
        var finestTriangles = 0;
        for (var i = 0; i < snapshots.Count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = snapshots[i];
            var mesh = patch.Geometry;
            var meshlets = MeshletBuilder.Build(mesh, maxVertices, maxTriangles, cancellationToken);
            var vertexIds = VertexIds(mesh.Vertices, vertexIdentities);
            boundaries[i] = Boundary(mesh.Indices, vertexIds);
            for (var v = 0; v < vertexIds.Length; v++) {
                if (vertexIds[v] == vertices.Count) { vertices.Add(mesh.Vertices[v]); }
            }
            var min = new float3(float.PositiveInfinity);
            var max = new float3(float.NegativeInfinity);
            foreach (var index in mesh.Indices) {
                min = math.min(min, mesh.Vertices[index].Position);
                max = math.max(max, mesh.Vertices[index].Position);
            }
            var triangleOffset = indices.Count / 3;
            nodes[i] = new(new(min, max), patch.LocalError, patch.Parent, patch.ChildOffset, patch.ChildCount,
                clusters.Count, meshlets.Meshlets.Length, triangleOffset, mesh.Indices.Length / 3);
            if (patch.ChildCount == 0) { finestTriangles = checked(finestTriangles + mesh.Indices.Length / 3); }
            foreach (var cluster in meshlets.Meshlets) {
                clusters.Add(cluster with {
                    VertexOffset = checked(cluster.VertexOffset + vertexReferences.Count),
                    TriangleOffset = checked(cluster.TriangleOffset + localIndices.Count)
                });
            }
            foreach (var vertex in meshlets.VertexIndices) { vertexReferences.Add((uint)vertexIds[vertex]); }
            foreach (var triangle in meshlets.SourceTriangleIndices) { sourceTriangles.Add(checked(triangle + (uint)triangleOffset)); }
            localIndices.AddRange(meshlets.TriangleIndices);
            foreach (var index in mesh.Indices) { indices.Add((uint)vertexIds[index]); }
        }

        for (var i = nodes.Length - 1; i >= 0; i--) {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[i];
            if (node.ChildCount == 0) { continue; }
            var combined = new Dictionary<(int, int), int>();
            var error = 0f;
            long triangles = 0;
            var min = node.Bounds.Min;
            var max = node.Bounds.Max;
            for (var c = node.ChildOffset; c < node.ChildOffset + node.ChildCount; c++) {
                foreach (var (edge, count) in boundaries[c]) { AddEdge(combined, edge, count); }
                error = MathF.Max(error, nodes[c].EstimatedSpatialError);
                triangles += nodes[c].TriangleCount;
                min = math.min(min, nodes[c].Bounds.Min);
                max = math.max(max, nodes[c].Bounds.Max);
            }
            if (node.TriangleCount > triangles || !EqualBoundary(boundaries[i], combined)) {
                throw new ArgumentException("Parent geometry must preserve the children's oriented boundary attributes and must not increase triangle count.", nameof(roots));
            }
            var accumulatedError = (double)error + node.EstimatedSpatialError;
            var roundedError = (float)accumulatedError;
            if (roundedError < accumulatedError || (error > 0 && node.EstimatedSpatialError > 0
                && roundedError == MathF.Max(error, node.EstimatedSpatialError))) {
                roundedError = MathF.BitIncrement(roundedError);
            }
            error = roundedError;
            if (!float.IsFinite(error)) { throw new ArgumentException("Accumulated patch error overflows.", nameof(roots)); }
            nodes[i] = node with { Bounds = new(min, max), EstimatedSpatialError = error };
        }
        var bounds = nodes.Length == 0 ? default : nodes[0].Bounds;
        for (var r = 1; r < roots.Length; r++) {
            bounds = new(math.min(bounds.Min, nodes[r].Bounds.Min), math.max(bounds.Max, nodes[r].Bounds.Max));
        }
        return new(nodes, roots.Length, finestTriangles, new(vertices.ToArray(), indices.ToArray(), bounds),
            new(clusters.ToArray(), vertexReferences.ToArray(), localIndices.ToArray(), sourceTriangles.ToArray()));

        void Add(MeshPatch patch, int parent)
        {
            ArgumentNullException.ThrowIfNull(patch);
            if (!seen.Add(patch)) { throw new ArgumentException("Patch input must be a forest without cycles or shared children.", nameof(roots)); }
            patches.Add((patch, parent));
        }
    }

    private static int[] VertexIds(ReadOnlySpan<MeshVertex> vertices, Dictionary<VertexIdentity, int> identities)
    {
        var ids = new int[vertices.Length];
        for (var i = 0; i < ids.Length; i++) {
            var v = vertices[i];
            if (!float.IsFinite(v.Normal.x) || !float.IsFinite(v.Normal.y) || !float.IsFinite(v.Normal.z)
                || !float.IsFinite(v.UV.x) || !float.IsFinite(v.UV.y) || !v.HasFiniteTangent) {
                throw new ArgumentException("Patch attributes must be finite.", nameof(vertices));
            }
            var identity = new VertexIdentity(v.Position.x, v.Position.y, v.Position.z,
                v.Normal.x, v.Normal.y, v.Normal.z, v.UV.x, v.UV.y, v.Tangent.x, v.Tangent.y, v.Tangent.z, v.Tangent.w);
            if (!identities.TryGetValue(identity, out ids[i])) { identities.Add(identity, ids[i] = identities.Count); }
        }
        return ids;
    }

    private static Dictionary<(int, int), int> Boundary(ReadOnlySpan<uint> indices, ReadOnlySpan<int> ids)
    {
        var boundary = new Dictionary<(int, int), int>();
        var edges = ArrayPool<ulong>.Shared.Rent(indices.Length);
        try {
            var count = 0;
            for (var t = 0; t < indices.Length; t += 3) {
                for (var c = 0; c < 3; c++) {
                    var a = ids[(int)indices[t + c]];
                    var b = ids[(int)indices[t + (c + 1) % 3]];
                    if (a == b) { continue; }
                    edges[count++] = ((ulong)(uint)System.Math.Min(a, b) << 32)
                        | ((ulong)(uint)System.Math.Max(a, b) << 1) | (a < b ? 0ul : 1ul);
                }
            }
            edges.AsSpan(0, count).Sort();
            for (var i = 0; i < count;) {
                var edge = edges[i] & ~1ul;
                var end = i + 1;
                while (end < count && (edges[end] & ~1ul) == edge) { end++; }
                if (end - i > 2) { throw new ArgumentException("Patch geometry contains a nonmanifold edge.", nameof(indices)); }
                if (end - i == 1) { boundary.Add(((int)(edge >> 32), (int)((uint)edge >> 1)), (edges[i] & 1) == 0 ? 1 : -1); }
                else if (edges[i] == edges[i + 1]) { throw new ArgumentException("Patch geometry contains inconsistent edge winding.", nameof(indices)); }
                i = end;
            }
            return boundary;
        }
        finally { ArrayPool<ulong>.Shared.Return(edges); }
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, (int, int) edge, int value)
    {
        var count = checked(edges.GetValueOrDefault(edge) + value);
        if (count == 0) { edges.Remove(edge); }
        else { edges[edge] = count; }
    }

    private static bool EqualBoundary(Dictionary<(int, int), int> a, Dictionary<(int, int), int> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private readonly record struct BuildPatch(MeshData Geometry, float LocalError, int Parent, int ChildOffset, int ChildCount);
    private readonly record struct VertexIdentity(float X, float Y, float Z, float Nx, float Ny, float Nz, float U, float V,
        float Tx, float Ty, float Tz, float Tw);
}
