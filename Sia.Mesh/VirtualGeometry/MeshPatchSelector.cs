using Sia.Math;

namespace Sia.Engine.Mesh;

public static class MeshPatchSelector
{
    public static MeshPatchSelection Select(MeshPatchTree tree, ReadOnlySpan<float4x4> objectToClip,
        uint width, uint height, float targetPixelError, MeshPatchBudget budget)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (width == 0 || height == 0 || !float.IsFinite(targetPixelError) || targetPixelError < 0
            || budget.MaxPatches < 0 || budget.MaxMeshlets < 0 || budget.MaxTriangles < 0) {
            throw new ArgumentOutOfRangeException(nameof(budget), "Selection requires a viewport, a finite nonnegative error target, and nonnegative budgets.");
        }
        foreach (var matrix in objectToClip) {
            if (!Finite(matrix.c0) || !Finite(matrix.c1) || !Finite(matrix.c2) || !Finite(matrix.c3)) {
                throw new ArgumentException("Projection transforms must be finite.", nameof(objectToClip));
            }
        }
        var nodes = tree.Nodes.Span;
        var selected = new bool[checked(nodes.Length * objectToClip.Length)];
        var errors = new float[selected.Length];
        var candidates = new PriorityQueue<int, (float Error, int Index)>();
        long patches = (long)tree.RootCount * objectToClip.Length;
        long meshlets = 0;
        long triangles = 0;
        for (var instance = 0; instance < objectToClip.Length; instance++) {
            for (var root = 0; root < tree.RootCount; root++) {
                var index = instance * nodes.Length + root;
                selected[index] = true;
                errors[index] = ProjectError(nodes[root], objectToClip[instance], width, height);
                meshlets += nodes[root].MeshletCount;
                triangles += nodes[root].TriangleCount;
                if (nodes[root].ChildCount > 0 && errors[index] > targetPixelError) {
                    candidates.Enqueue(index, (-errors[index], index));
                }
            }
        }
        var unreachable = patches > budget.MaxPatches || meshlets > budget.MaxMeshlets || triangles > budget.MaxTriangles;
        var limited = unreachable;
        while (!unreachable && candidates.TryDequeue(out var index, out _)) {
            var patch = index % nodes.Length;
            var instance = index / nodes.Length;
            var node = nodes[patch];
            long childMeshlets = 0;
            long childTriangles = 0;
            for (var c = node.ChildOffset; c < node.ChildOffset + node.ChildCount; c++) {
                childMeshlets += nodes[c].MeshletCount;
                childTriangles += nodes[c].TriangleCount;
            }
            var nextPatches = patches - 1 + node.ChildCount;
            var nextMeshlets = meshlets - node.MeshletCount + childMeshlets;
            var nextTriangles = triangles - node.TriangleCount + childTriangles;
            if (nextPatches > budget.MaxPatches || nextMeshlets > budget.MaxMeshlets || nextTriangles > budget.MaxTriangles) {
                limited = true;
                continue;
            }
            selected[index] = false;
            patches = nextPatches;
            meshlets = nextMeshlets;
            triangles = nextTriangles;
            for (var c = node.ChildOffset; c < node.ChildOffset + node.ChildCount; c++) {
                var childIndex = instance * nodes.Length + c;
                selected[childIndex] = true;
                errors[childIndex] = ProjectError(nodes[c], objectToClip[instance], width, height);
                if (nodes[c].ChildCount > 0 && errors[childIndex] > targetPixelError) {
                    candidates.Enqueue(childIndex, (-errors[childIndex], childIndex));
                }
            }
        }
        var result = new SelectedMeshPatch[checked((int)patches)];
        var next = 0;
        var maximumError = 0f;
        for (var i = 0; i < selected.Length; i++) {
            if (!selected[i]) { continue; }
            result[next++] = new(i / nodes.Length, i % nodes.Length);
            maximumError = MathF.Max(maximumError, errors[i]);
        }
        return new(result, checked((int)meshlets), checked((int)triangles), maximumError, limited, unreachable);
    }

    private static float ProjectError(in MeshPatchNode node, in float4x4 matrix, uint width, uint height)
    {
        if (node.EstimatedSpatialError == 0) { return 0; }
        double minW = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = 0, maxY = 0;
        for (var corner = 0; corner < 8; corner++) {
            var point = new float4((corner & 1) == 0 ? node.Bounds.Min.x : node.Bounds.Max.x,
                (corner & 2) == 0 ? node.Bounds.Min.y : node.Bounds.Max.y,
                (corner & 4) == 0 ? node.Bounds.Min.z : node.Bounds.Max.z, 1);
            var clip = math.mul(matrix, point);
            if (!Finite(clip) || clip.w <= 0) { return float.PositiveInfinity; }
            minW = System.Math.Min(minW, clip.w);
            minZ = System.Math.Min(minZ, clip.z);
            maxX = System.Math.Max(maxX, System.Math.Abs((double)clip.x / clip.w));
            maxY = System.Math.Max(maxY, System.Math.Abs((double)clip.y / clip.w));
        }
        var error = (double)node.EstimatedSpatialError;
        var wError = error * Length(matrix.c0.w, matrix.c1.w, matrix.c2.w);
        var zError = error * Length(matrix.c0.z, matrix.c1.z, matrix.c2.z);
        if (minW <= wError || minZ <= zError) { return float.PositiveInfinity; }
        var dx = error * (Length(matrix.c0.x, matrix.c1.x, matrix.c2.x) + maxX * Length(matrix.c0.w, matrix.c1.w, matrix.c2.w));
        var dy = error * (Length(matrix.c0.y, matrix.c1.y, matrix.c2.y) + maxY * Length(matrix.c0.w, matrix.c1.w, matrix.c2.w));
        return (float)(0.5 * System.Math.Max(width * dx, height * dy) / (minW - wError));
    }

    private static double Length(double x, double y, double z) => System.Math.Sqrt(x * x + y * y + z * z);
    private static bool Finite(float4 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
}
