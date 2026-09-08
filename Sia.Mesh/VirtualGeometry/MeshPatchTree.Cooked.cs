using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchTree
{
    internal static MeshPatchTree Restore(MeshPatchNode[] nodes, int roots, int finest, MeshData geometry,
        MeshletData meshlets, CancellationToken cancellationToken)
    {
        Require(roots >= 0 && roots <= nodes.Length && (nodes.Length == 0) == (roots == 0), "Invalid patch root count.");
        Require(Valid(geometry.Bounds), "Invalid geometry bounds.");
        foreach (var vertex in geometry.Vertices) {
            cancellationToken.ThrowIfCancellationRequested();
            Require(Finite(vertex.Position) && Finite(vertex.Normal) && float.IsFinite(vertex.UV.x) && float.IsFinite(vertex.UV.y)
                && vertex.HasFiniteTangent,
                "Patch vertex attributes must be finite.");
        }
        foreach (var index in geometry.Indices) { Require(index < geometry.Vertices.Length, "Geometry index is out of range."); }
        foreach (var index in meshlets.VertexIndices) { Require(index < geometry.Vertices.Length, "Meshlet vertex reference is out of range."); }
        var nextChild = roots;
        var nextTriangle = 0;
        var nextMeshlet = 0;
        long leafTriangles = 0;
        for (var i = 0; i < nodes.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[i];
            Require(i < roots ? node.Parent == -1 : node.Parent >= 0 && node.Parent < i, "Invalid patch parent.");
            Require(Valid(node.Bounds) && Contains(geometry.Bounds, node.Bounds.Min) && Contains(geometry.Bounds, node.Bounds.Max)
                && float.IsFinite(node.EstimatedSpatialError) && node.EstimatedSpatialError >= 0
                && (node.ChildCount != 0 || node.EstimatedSpatialError == 0), "Invalid patch bounds or error.");
            Require(node.ChildOffset == nextChild && node.ChildCount >= 0 && node.ChildCount <= nodes.Length - nextChild,
                "Patch children must form an ordered forest.");
            long childTriangles = 0;
            for (var c = nextChild; c < nextChild + node.ChildCount; c++) {
                var child = nodes[c];
                Require(c > i && child.Parent == i, "Patch hierarchy contains a cycle or shared child.");
                Require(Contains(node.Bounds, child.Bounds.Min) && Contains(node.Bounds, child.Bounds.Max)
                    && node.EstimatedSpatialError >= child.EstimatedSpatialError, "Parent bounds and error must cover every child.");
                childTriangles += child.TriangleCount;
            }
            Require(node.ChildCount == 0 || node.TriangleCount <= childTriangles, "Parent geometry increases triangle count.");
            nextChild += node.ChildCount;
            Require(node.TriangleOffset == nextTriangle && node.TriangleCount > 0
                && node.TriangleCount <= geometry.Indices.Length / 3 - nextTriangle
                && node.MeshletOffset == nextMeshlet && node.MeshletCount > 0
                && node.MeshletCount <= meshlets.Meshlets.Length - nextMeshlet, "Invalid patch geometry range.");
            for (var t = nextTriangle * 3; t < (nextTriangle + node.TriangleCount) * 3; t++) {
                Require(Contains(node.Bounds, geometry.Vertices[geometry.Indices[t]].Position), "Patch bounds exclude its geometry.");
            }
            var meshletTriangles = 0L;
            for (var m = nextMeshlet; m < nextMeshlet + node.MeshletCount; m++) { meshletTriangles += meshlets.Meshlets[m].TriangleCount; }
            Require(meshletTriangles == node.TriangleCount, "Patch meshlets do not cover its triangles.");
            if (node.ChildCount == 0) { leafTriangles += node.TriangleCount; }
            nextTriangle += node.TriangleCount;
            nextMeshlet += node.MeshletCount;
        }
        Require(nextChild == nodes.Length && nextTriangle == geometry.Indices.Length / 3
            && nextMeshlet == meshlets.Meshlets.Length && leafTriangles == finest, "Incomplete patch forest or geometry stream.");
        Require(nodes.Length != 0 || geometry.Vertices.Length == 0, "Empty patch assets cannot contain unused geometry.");
        ValidateMeshlets(nodes, geometry, meshlets, cancellationToken);
        var ids = VertexIds(geometry.Vertices, new(geometry.Vertices.Length));
        var boundaries = new Dictionary<(int, int), int>[nodes.Length];
        try {
            for (var i = nodes.Length - 1; i >= 0; i--) {
                cancellationToken.ThrowIfCancellationRequested();
                var node = nodes[i];
                var boundary = Boundary(geometry.Indices.AsSpan(node.TriangleOffset * 3, node.TriangleCount * 3), ids);
                if (node.ChildCount != 0) {
                    var combined = new Dictionary<(int, int), int>();
                    for (var c = node.ChildOffset; c < node.ChildOffset + node.ChildCount; c++) {
                        foreach (var (edge, count) in boundaries[c]) { AddEdge(combined, edge, count); }
                        boundaries[c] = null!;
                    }
                    Require(EqualBoundary(boundary, combined), "Parent geometry changes the children's oriented attribute boundary.");
                }
                boundaries[i] = boundary;
            }
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid patch topology.", error); }
        return new(nodes, roots, finest, geometry, meshlets);
    }

    private static void ValidateMeshlets(MeshPatchNode[] nodes, MeshData geometry, MeshletData data, CancellationToken cancellationToken)
    {
        var nextVertex = 0;
        var nextTriangle = 0;
        var seen = new bool[geometry.Indices.Length / 3];
        foreach (var node in nodes) {
            for (var m = node.MeshletOffset; m < node.MeshletOffset + node.MeshletCount; m++) {
                cancellationToken.ThrowIfCancellationRequested();
                var cluster = data.Meshlets[m];
                Require(cluster.VertexOffset == nextVertex && cluster.VertexCount is >= 1 and <= 256
                    && cluster.VertexCount <= data.VertexIndices.Length - nextVertex
                    && cluster.TriangleOffset == nextTriangle * 3 && cluster.TriangleCount is >= 1 and <= 512
                    && cluster.TriangleCount <= seen.Length - nextTriangle, "Invalid meshlet range.");
                var bounds = cluster.Bounds;
                Require(Valid(bounds.Box) && Finite(bounds.Center) && float.IsFinite(bounds.Radius) && bounds.Radius >= 0
                    && Finite(bounds.ConeAxis) && float.IsFinite(bounds.ConeCutoff) && bounds.ConeCutoff is >= 0 and <= 1,
                    "Invalid meshlet bounds.");
                for (var v = nextVertex; v < nextVertex + cluster.VertexCount; v++) {
                    var position = geometry.Vertices[data.VertexIndices[v]].Position;
                    var delta = Double(position) - Double(bounds.Center);
                    Require(Contains(bounds.Box, position) && math.dot(delta, delta) <= (double)bounds.Radius * bounds.Radius,
                        "Meshlet bounds exclude a referenced vertex.");
                }
                var axis = Double(bounds.ConeAxis);
                if (bounds.ConeCutoff < 1) {
                    Require(System.Math.Abs(math.dot(axis, axis) - 1) <= 1e-6, "Meshlet cone axis must be normalized.");
                }
                for (var t = nextTriangle; t < nextTriangle + cluster.TriangleCount; t++) {
                    var source = data.SourceTriangleIndices[t];
                    Require(source >= node.TriangleOffset && source < (long)node.TriangleOffset + node.TriangleCount && !seen[source],
                        "Meshlets must cover each patch triangle exactly once.");
                    seen[source] = true;
                    for (var c = 0; c < 3; c++) {
                        var local = data.TriangleIndices[t * 3 + c];
                        Require(local < cluster.VertexCount && data.VertexIndices[nextVertex + local] == geometry.Indices[(int)source * 3 + c],
                            "Meshlet triangle differs from its source geometry.");
                    }
                    if (bounds.ConeCutoff < 1) {
                        var a = Double(geometry.Vertices[geometry.Indices[(int)source * 3]].Position);
                        var b = Double(geometry.Vertices[geometry.Indices[(int)source * 3 + 1]].Position);
                        var c = Double(geometry.Vertices[geometry.Indices[(int)source * 3 + 2]].Position);
                        var normal = math.cross(b - a, c - a);
                        var length = math.length(normal);
                        if (length > 0) {
                            normal /= length;
                            Require(math.dot(normal, axis) > 0 && math.length(math.cross(normal, axis)) <= bounds.ConeCutoff,
                                "Meshlet cone excludes a triangle normal.");
                        }
                    }
                }
                nextVertex += cluster.VertexCount;
                nextTriangle += cluster.TriangleCount;
            }
        }
        Require(nextVertex == data.VertexIndices.Length && nextTriangle == seen.Length, "Incomplete meshlet streams.");
    }

    private static double3 Double(float3 value) => new(value.x, value.y, value.z);
    private static bool Finite(float3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Valid(Aabb box) => Finite(box.Min) && Finite(box.Max)
        && box.Min.x <= box.Max.x && box.Min.y <= box.Max.y && box.Min.z <= box.Max.z;
    private static bool Contains(Aabb box, float3 point) => point.x >= box.Min.x && point.x <= box.Max.x
        && point.y >= box.Min.y && point.y <= box.Max.y && point.z >= box.Min.z && point.z <= box.Max.z;
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
