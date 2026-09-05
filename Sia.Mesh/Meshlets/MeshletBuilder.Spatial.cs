using Sia.Math;

namespace Sia.Engine.Mesh;

// Algorithm: https://github.com/zeux/meshoptimizer/blob/v1.2/src/clusterizer.cpp
// License: https://github.com/zeux/meshoptimizer/blob/v1.2/LICENSE.md
public static partial class MeshletBuilder
{
    private const int k_SpatialLeafSize = 16;

    private struct SpatialNode
    {
        public int Offset, Count, Left, Right, Parent, Remaining, Axis;
        public float Split;
    }

    private readonly struct CenterComparer(float3[] centers, int axis) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var comparison = Coordinate(centers[a], axis).CompareTo(Coordinate(centers[b], axis));
            return comparison != 0 ? comparison : a.CompareTo(b);
        }
    }

    private static SpatialNode[] BuildSpatialTree(
        float3[] centers, CancellationToken cancellationToken, out int[] order, out int[] leaves, out float3 corner)
    {
        order = new int[centers.Length];
        leaves = new int[centers.Length];
        corner = centers[0];
        for (var i = 0; i < order.Length; i++) {
            if ((i & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            order[i] = i;
            corner = math.min(corner, centers[i]);
        }
        var nodes = new SpatialNode[checked((centers.Length / (k_SpatialLeafSize / 2) + 1) * 2 - 1)];
        var nodeCount = 0;
        BuildSpatialNode(nodes, order, leaves, centers, 0, order.Length, -1, ref nodeCount, cancellationToken);
        return nodes;
    }

    private static int BuildSpatialNode(
        SpatialNode[] nodes, int[] order, int[] leaves, float3[] centers,
        int start, int count, int parent, ref int nodeCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = nodeCount++;
        nodes[index] = new SpatialNode { Offset = start, Count = count, Parent = parent, Remaining = count };
        if (count <= k_SpatialLeafSize) {
            for (var i = start; i < start + count; i++) {
                leaves[order[i]] = index;
            }
            return index;
        }
        var min = centers[order[start]];
        var max = min;
        for (var i = start + 1; i < start + count; i++) {
            if ((i & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            min = math.min(min, centers[order[i]]);
            max = math.max(max, centers[order[i]]);
        }
        var x = (double)max.x - min.x;
        var y = (double)max.y - min.y;
        var z = (double)max.z - min.z;
        var axis = x >= y && x >= z ? 0 : y >= z ? 1 : 2;
        var leftCount = count / 2;
        SelectMedian(order.AsSpan(start, count), leftCount, new CenterComparer(centers, axis));
        nodes[index].Count = 0;
        nodes[index].Axis = axis;
        nodes[index].Split = Coordinate(centers[order[start + leftCount]], axis);
        nodes[index].Left = BuildSpatialNode(nodes, order, leaves, centers, start, leftCount, index, ref nodeCount, cancellationToken);
        nodes[index].Right = BuildSpatialNode(nodes, order, leaves, centers, start + leftCount, count - leftCount, index, ref nodeCount, cancellationToken);
        return index;
    }

    private static void SelectMedian(Span<int> order, int middle, CenterComparer comparer)
    {
        var left = 0;
        var right = order.Length - 1;
        var budget = 2 * System.Numerics.BitOperations.Log2((uint)order.Length);
        while (right - left >= k_SpatialLeafSize && budget-- > 0) {
            var center = left + (right - left) / 2;
            if (comparer.Compare(order[left], order[center]) > 0) {
                (order[left], order[center]) = (order[center], order[left]);
            }
            if (comparer.Compare(order[center], order[right]) > 0) {
                (order[center], order[right]) = (order[right], order[center]);
            }
            if (comparer.Compare(order[left], order[center]) > 0) {
                (order[left], order[center]) = (order[center], order[left]);
            }
            var pivot = order[center];
            var i = left;
            var j = right;
            while (i <= j) {
                while (i <= j && comparer.Compare(order[i], pivot) < 0) {
                    i++;
                }
                while (i <= j && comparer.Compare(order[j], pivot) > 0) {
                    j--;
                }
                if (i <= j) {
                    (order[i], order[j]) = (order[j], order[i]);
                    i++;
                    j--;
                }
            }
            if (middle <= j) {
                right = j;
            }
            else if (middle >= i) {
                left = i;
            }
            else {
                return;
            }
        }
        if (right - left >= k_SpatialLeafSize) {
            order.Slice(left, right - left + 1).Sort(comparer);
            return;
        }
        for (var i = left + 1; i <= right; i++) {
            var value = order[i];
            var j = i;
            while (j > left && comparer.Compare(value, order[j - 1]) < 0) {
                order[j] = order[j - 1];
                j--;
            }
            order[j] = value;
        }
    }

    private static int FindSpatialTriangle(
        SpatialNode[] nodes, int[] order, float3[] centers, bool[] emitted, double x, double y, double z)
    {
        var best = -1;
        var distance = double.PositiveInfinity;
        FindSpatialTriangle(nodes, order, centers, emitted, 0, x, y, z, ref best, ref distance);
        return best;
    }

    private static void FindSpatialTriangle(
        SpatialNode[] nodes, int[] order, float3[] centers, bool[] emitted, int index,
        double x, double y, double z, ref int best, ref double distance)
    {
        ref var node = ref nodes[index];
        if (node.Remaining == 0) {
            return;
        }
        if (node.Count > 0) {
            for (var i = node.Offset; i < node.Offset + node.Count; i++) {
                var triangle = order[i];
                if (emitted[triangle]) {
                    continue;
                }
                var dx = centers[triangle].x - x;
                var dy = centers[triangle].y - y;
                var dz = centers[triangle].z - z;
                var candidateDistance = dx * dx + dy * dy + dz * dz;
                if (candidateDistance < distance) {
                    distance = candidateDistance;
                    best = triangle;
                }
            }
            return;
        }
        var plane = (node.Axis == 0 ? x : node.Axis == 1 ? y : z) - node.Split;
        var near = plane < 0 ? node.Left : node.Right;
        var far = plane < 0 ? node.Right : node.Left;
        FindSpatialTriangle(nodes, order, centers, emitted, near, x, y, z, ref best, ref distance);
        if (plane * plane < distance) {
            FindSpatialTriangle(nodes, order, centers, emitted, far, x, y, z, ref best, ref distance);
        }
    }

    private static void RemoveSpatialTriangle(SpatialNode[] nodes, int leaf)
    {
        for (var index = leaf; index >= 0; index = nodes[index].Parent) {
            nodes[index].Remaining--;
        }
    }

    private static float Coordinate(float3 center, int axis) => axis == 0 ? center.x : axis == 1 ? center.y : center.z;
}
