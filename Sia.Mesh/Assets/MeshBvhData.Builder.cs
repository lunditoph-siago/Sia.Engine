using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshBvhData
{
    private sealed class Builder
    {
        private const int BinCount = 16;
        private readonly MeshSource _source;
        private readonly MeshBvhBuildSettings _settings;
        private readonly ulong _maximumBytes;
        private readonly ulong _geometryBytes;
        private readonly CancellationToken _cancellation;
        private readonly float3[] _positions;
        private readonly MeshBvhTriangle[] _triangles;
        private readonly double3[] _centroids;
        private readonly List<MeshBvhNode> _nodes = [];
        private readonly Comparison<MeshBvhTriangle>[] _comparisons;

        public Builder(MeshSource source, MeshBvhBuildSettings settings, ulong maximumBytes,
            ulong geometryBytes, CancellationToken cancellation)
        {
            _source = source;
            _settings = settings;
            _maximumBytes = maximumBytes;
            _geometryBytes = geometryBytes;
            _cancellation = cancellation;
            _positions = new float3[source.Vertices.Length];
            for (var i = 0; i < _positions.Length; i++) {
                if ((i & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                _positions[i] = source.Vertices[i].Position;
            }
            _triangles = new MeshBvhTriangle[source.Indices.Length / 3];
            _centroids = new double3[_triangles.Length];
            var primitive = 0;
            foreach (var section in source.Sections) {
                var end = section.FirstIndex + section.IndexCount;
                for (var i = section.FirstIndex; i < end; i += 3) {
                    if ((primitive & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                    var triangle = new MeshBvhTriangle(source.Indices[i], source.Indices[i + 1], source.Indices[i + 2],
                        primitive, section.MaterialSlot);
                    _triangles[primitive] = triangle;
                    _centroids[primitive++] = (Position(triangle.A) + Position(triangle.B) + Position(triangle.C)) / 3;
                }
            }
            _comparisons = [(a, b) => Compare(a, b, 0), (a, b) => Compare(a, b, 1), (a, b) => Compare(a, b, 2)];
        }

        public MeshBvhData Build()
        {
            BuildNode(0, _triangles.Length, 0);
            var geometryHash = HashGeometry(_source, _cancellation);
            return new(_positions, _triangles, _nodes.ToArray(), _settings, geometryHash);
        }

        private void BuildNode(int start, int count, int depth)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_geometryBytes + ((ulong)_nodes.Count + 1) * (ulong)Unsafe.SizeOf<MeshBvhNode>() > _maximumBytes)
                throw new ArgumentOutOfRangeException("maximumBytes", "Mesh BVH nodes exceed the payload budget.");
            var nodeIndex = _nodes.Count;
            _nodes.Add(default);
            var bounds = EmptyBounds();
            var centroidLo = new double3(double.PositiveInfinity);
            var centroidHi = new double3(double.NegativeInfinity);
            for (var i = start; i < start + count; i++) {
                if ((i & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                bounds = Union(bounds, TriangleBounds(_triangles[i]));
                var center = _centroids[_triangles[i].SourceTriangle];
                centroidLo = math.min(centroidLo, center);
                centroidHi = math.max(centroidHi, center);
            }
            if (count <= _settings.MaxLeafTriangles) {
                _nodes[nodeIndex] = new(bounds, _nodes.Count, start, count);
                return;
            }

            // Bound recursion even for adversarial SAH splits; median splitting is deterministic.
            var split = depth < 48 ? FindSplit(start, count, centroidLo, centroidHi) : (-1, -1);
            var middle = start;
            if (split.Item1 >= 0) {
                var last = start + count;
                while (middle < last) {
                    if (((last - middle) & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                    var triangle = _triangles[middle];
                    if (Bin(_centroids[triangle.SourceTriangle][split.Item1], centroidLo[split.Item1],
                        centroidHi[split.Item1]) <= split.Item2) middle++;
                    else { last--; (_triangles[middle], _triangles[last]) = (_triangles[last], triangle); }
                }
            }
            if (middle == start || middle == start + count) {
                var extent = centroidHi - centroidLo;
                var axis = extent.y > extent.x ? 1 : 0;
                if (extent.z > extent[axis]) axis = 2;
                _triangles.AsSpan(start, count).Sort(_comparisons[axis]);
                middle = start + count / 2;
            }
            BuildNode(start, middle - start, depth + 1);
            BuildNode(middle, start + count - middle, depth + 1);
            _nodes[nodeIndex] = new(bounds, _nodes.Count, 0, 0);
        }

        private (int Axis, int Bin) FindSplit(int start, int count, double3 lo, double3 hi)
        {
            var best = double.PositiveInfinity;
            var selected = (-1, -1);
            Span<BuildBin> bins = stackalloc BuildBin[BinCount];
            Span<BuildBin> suffix = stackalloc BuildBin[BinCount];
            for (var axis = 0; axis < 3; axis++) {
                if (lo[axis] == hi[axis]) continue;
                for (var b = 0; b < BinCount; b++) bins[b] = new(EmptyBounds(), 0);
                for (var i = start; i < start + count; i++) {
                    if ((i & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                    var triangle = _triangles[i];
                    var bin = Bin(_centroids[triangle.SourceTriangle][axis], lo[axis], hi[axis]);
                    bins[bin] = new(Union(bins[bin].Bounds, TriangleBounds(triangle)), bins[bin].Count + 1);
                }
                var right = new BuildBin(EmptyBounds(), 0);
                for (var b = BinCount - 1; b >= 0; b--) {
                    right = Join(right, bins[b]);
                    suffix[b] = right;
                }
                var left = new BuildBin(EmptyBounds(), 0);
                for (var b = 0; b < BinCount - 1; b++) {
                    left = Join(left, bins[b]);
                    right = suffix[b + 1];
                    if (left.Count == 0 || right.Count == 0) continue;
                    var cost = Area(left.Bounds) * left.Count + Area(right.Bounds) * right.Count;
                    if (cost < best) { best = cost; selected = (axis, b); }
                }
            }
            return selected;
        }

        private int Compare(MeshBvhTriangle a, MeshBvhTriangle b, int axis)
        {
            var order = _centroids[a.SourceTriangle][axis].CompareTo(_centroids[b.SourceTriangle][axis]);
            return order == 0 ? a.SourceTriangle.CompareTo(b.SourceTriangle) : order;
        }

        private double3 Position(uint index)
        {
            var p = _positions[index];
            return new(p.x, p.y, p.z);
        }

        private Aabb TriangleBounds(MeshBvhTriangle triangle)
        {
            var a = _positions[triangle.A]; var b = _positions[triangle.B]; var c = _positions[triangle.C];
            return new(math.min(a, math.min(b, c)), math.max(a, math.max(b, c)));
        }

        private static int Bin(double center, double lo, double hi)
            => System.Math.Min(BinCount - 1, (int)((center - lo) / (hi - lo) * BinCount));
        private static Aabb EmptyBounds() => new(new(float.PositiveInfinity), new(float.NegativeInfinity));
        private static Aabb Union(Aabb a, Aabb b) => new(math.min(a.Min, b.Min), math.max(a.Max, b.Max));
        private static BuildBin Join(BuildBin a, BuildBin b) => new(Union(a.Bounds, b.Bounds), a.Count + b.Count);
        private static double Area(Aabb bounds)
        {
            var x = (double)bounds.Max.x - bounds.Min.x;
            var y = (double)bounds.Max.y - bounds.Min.y;
            var z = (double)bounds.Max.z - bounds.Min.z;
            return 2 * (x * y + y * z + z * x);
        }

        private readonly record struct BuildBin(Aabb Bounds, int Count);
    }
}
