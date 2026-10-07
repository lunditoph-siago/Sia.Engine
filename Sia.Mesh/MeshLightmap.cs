using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>Pixel allocation including the empty filtering border, in a mesh-local lightmap.</summary>
public readonly record struct MeshLightmapChart(int X, int Y, int Width, int Height);

/// <summary>Generated coordinates and chart ownership in the original triangle order.</summary>
public sealed record MeshLightmapLayout(MeshData Mesh, int Resolution, int Padding,
    ReadOnlyMemory<MeshLightmapChart> Charts, ReadOnlyMemory<int> TriangleCharts);

public static class MeshLightmap
{
    /// <summary>
    /// Reuses nonoverlapping source-UV parameterizations for curved regions, projects planar
    /// regions geometrically, and packs disjoint padded rectangles. Material coordinates and
    /// vertex attributes are retained. Invalid parameterizations conservatively split.
    /// </summary>
    public static MeshLightmapLayout Generate(MeshData mesh, int resolution = 128, int padding = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        if (resolution is < 8 or > 8192) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (padding < 1 || padding > (resolution - 2) / 2) throw new ArgumentOutOfRangeException(nameof(padding));
        cancellationToken.ThrowIfCancellationRequested();
        if (mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0
            || mesh.Indices.Length > 3_000_000 || mesh.Vertices.Length > 3_000_000)
            throw new ArgumentException("Lightmap generation requires one to one million triangles.", nameof(mesh));
        var positions = new double3[mesh.Vertices.Length];
        double3 min = new(double.PositiveInfinity), max = new(double.NegativeInfinity);
        for (var i = 0; i < positions.Length; i++) {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var v = mesh.Vertices[i];
            if (!math.all(math.isfinite(v.Position)) || !math.all(math.isfinite(v.Normal))
                || !math.all(math.isfinite(v.UV)) || !v.HasFiniteTangent)
                throw new ArgumentException("Lightmap source attributes must be finite.", nameof(mesh));
            positions[i] = new(v.Position.x, v.Position.y, v.Position.z);
            min = math.min(min, positions[i]);
            max = math.max(max, positions[i]);
        }
        var triangleCount = mesh.Indices.Length / 3;
        var normals = new double3[triangleCount];
        var adjacent = new int[mesh.Indices.Length];
        Array.Fill(adjacent, -1);
        var edges = new Dictionary<(uint, uint), (int Corner, int Second, uint From)>();
        for (var t = 0; t < triangleCount; t++) {
            if ((t & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var c = 0; c < 3; c++)
                if (mesh.Indices[t * 3 + c] >= positions.Length)
                    throw new ArgumentException("Lightmap source index is out of range.", nameof(mesh));
            var a = positions[mesh.Indices[t * 3]];
            var b = positions[mesh.Indices[t * 3 + 1]];
            var cPosition = positions[mesh.Indices[t * 3 + 2]];
            var n = math.cross(b - a, cPosition - a);
            var length = math.length(n);
            if (!double.IsFinite(length) || length <= 0)
                throw new ArgumentException("Lightmap source contains a degenerate triangle.", nameof(mesh));
            normals[t] = n / length;
            for (var c = 0; c < 3; c++) {
                var from = mesh.Indices[t * 3 + c];
                var to = mesh.Indices[t * 3 + (c + 1) % 3];
                var key = from < to ? (from, to) : (to, from);
                if (!edges.TryGetValue(key, out var edge)) {
                    edges.Add(key, (t * 3 + c, -1, from));
                } else {
                    if (edge.Second >= 0 || edge.From == from)
                        throw new ArgumentException("Lightmap source edges must be manifold and consistently wound.", nameof(mesh));
                    adjacent[t * 3 + c] = edge.Corner / 3;
                    adjacent[edge.Corner] = t;
                    edges[key] = edge with { Second = t * 3 + c };
                }
            }
        }
        ConnectAttributeSeams(mesh, adjacent, edges, cancellationToken);
        double UVArea(int t)
        {
            var a = mesh.Vertices[mesh.Indices[t * 3]].UV;
            var b = mesh.Vertices[mesh.Indices[t * 3 + 1]].UV;
            var c = mesh.Vertices[mesh.Indices[t * 3 + 2]].UV;
            return Cross(new double2((double)b.x - a.x, (double)b.y - a.y),
                new double2((double)c.x - a.x, (double)c.y - a.y));
        }
        // A seed plane prevents gradual curvature from joining a surface without a
        // usable source parameterization. Source UVs are copied into the independent
        // lightmap channel only after positive-area overlap validation.
        var tolerance = math.length(max - min) * 1e-7;
        var assigned = new bool[triangleCount];
        var charts = new List<Chart>();
        var queue = new Queue<int>();
        for (var seed = 0; seed < triangleCount; seed++) {
            if (assigned[seed]) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var chart = new Chart(normals[seed], positions[mesh.Indices[seed * 3]]);
            var uvArea = UVArea(seed);
            chart.SourceCoordinates = uvArea != 0;
            chart.UVSign = uvArea < 0 ? -1 : 1;
            var originUV = mesh.Vertices[mesh.Indices[seed * 3]].UV;
            chart.UVOrigin = new(originUV.x, originUV.y);
            assigned[seed] = true;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out var t)) {
                if ((chart.Triangles.Count & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                chart.Triangles.Add(t);
                for (var e = 0; e < 3; e++) {
                    var other = adjacent[t * 3 + e];
                    if (other < 0 || assigned[other]) continue;
                    if (chart.SourceCoordinates) {
                        if (UVArea(other) * chart.UVSign <= 0) continue;
                    } else if (!Planar(other, chart)) continue;
                    assigned[other] = true;
                    queue.Enqueue(other);
                }
            }
            if (chart.SourceCoordinates && chart.Triangles.All(t => Planar(t, chart))) chart.SourceCoordinates = false;
            chart.SetBounds(mesh.Indices, positions, mesh.Vertices);
            if (OverlapsOrExceedsWorkBudget(chart, mesh.Indices, positions, mesh.Vertices, cancellationToken)) {
                foreach (var t in chart.Triangles) {
                    var single = new Chart(normals[t], positions[mesh.Indices[t * 3]]);
                    single.Triangles.Add(t);
                    single.SetBounds(mesh.Indices, positions, mesh.Vertices);
                    charts.Add(single);
                }
            } else charts.Add(chart);
        }
        double pass;
        while (true) {
            var minimumEdge = 2 + 2 * padding;
            if ((long)charts.Count * minimumEdge * minimumEdge > (long)resolution * resolution)
                throw new InvalidOperationException("Lightmap resolution cannot hold all charts with the requested padding.");
            // Stable shelf packing with a bounded density search. Scale is uniform except
            // for a minimum one-pixel interval along a thin axis. The initial packer does
            // not rotate charts or pack their silhouettes.
            var order = Enumerable.Range(0, charts.Count).OrderByDescending(i => charts[i].Size.y)
                .ThenByDescending(i => charts[i].Size.x).ThenBy(i => i).ToArray();
            if (!Pack(charts, order, resolution, padding, 0, cancellationToken))
                throw new InvalidOperationException("Lightmap resolution cannot hold all padded chart rectangles.");
            var largest = charts.Max(c => System.Math.Max(c.Size.x, c.Size.y));
            pass = 0;
            var fail = resolution / largest;
            for (var iteration = 0; iteration < 24; iteration++) {
                var scale = (pass + fail) * .5;
                if (Pack(charts, order, resolution, padding, scale, cancellationToken)) pass = scale;
                else fail = scale;
            }
            Pack(charts, order, resolution, padding, pass, cancellationToken);
            // Verify the actual float transport after packing, rather than assuming a
            // positive double parameterization survives its final atlas offset. Split
            // only affected charts and project their triangles along the longest edge.
            var replacements = new List<Chart>();
            var changed = false;
            foreach (var chart in charts) {
                cancellationToken.ThrowIfCancellationRequested();
                var scale = math.max(new double2(pass), 1 / chart.Size);
                float2 UV(uint source) {
                    var p = chart.Project(source, positions, mesh.Vertices) - chart.Min;
                    return new((float)((chart.Allocation.X + padding + .5 + p.x * scale.x) / resolution),
                        (float)((chart.Allocation.Y + padding + .5 + p.y * scale.y) / resolution));
                }
                var collapsed = chart.Triangles.Any(t => {
                    var a = UV(mesh.Indices[t * 3]);
                    var b = UV(mesh.Indices[t * 3 + 1]);
                    var c = UV(mesh.Indices[t * 3 + 2]);
                    return Cross(new double2((double)b.x - a.x, (double)b.y - a.y),
                        new double2((double)c.x - a.x, (double)c.y - a.y)) <= 0;
                });
                if (!collapsed) { replacements.Add(chart); continue; }
                if (chart.TriangleBasis)
                    throw new InvalidOperationException("Lightmap triangle cannot retain orientation in float coordinates.");
                foreach (var t in chart.Triangles) {
                    var single = new Chart(normals[t], positions[mesh.Indices[t * 3]]);
                    single.Triangles.Add(t);
                    single.AlignTriangle(mesh.Indices, positions);
                    single.SetBounds(mesh.Indices, positions, mesh.Vertices);
                    replacements.Add(single);
                }
                changed = true;
            }
            if (!changed) break;
            charts = replacements;
        }
        var vertices = new List<MeshVertex>();
        var indices = new uint[mesh.Indices.Length];
        var triangleCharts = new int[triangleCount];
        var allocations = new MeshLightmapChart[charts.Count];
        for (var id = 0; id < charts.Count; id++) {
            cancellationToken.ThrowIfCancellationRequested();
            var chart = charts[id];
            allocations[id] = chart.Allocation;
            var vertexMap = new Dictionary<uint, uint>();
            // Keep thin charts representable without expanding their long axis beyond
            // the atlas. Meaningful physical texel density remains caller-owned.
            var scale = math.max(new double2(pass), 1 / chart.Size);
            foreach (var t in chart.Triangles) {
                triangleCharts[t] = id;
                for (var c = 0; c < 3; c++) {
                    var source = mesh.Indices[t * 3 + c];
                    if (!vertexMap.TryGetValue(source, out var target)) {
                        var p = chart.Project(source, positions, mesh.Vertices) - chart.Min;
                        var uv = new float2((float)((chart.Allocation.X + padding + .5 + p.x * scale.x) / resolution),
                            (float)((chart.Allocation.Y + padding + .5 + p.y * scale.y) / resolution));
                        target = (uint)vertices.Count;
                        vertexMap.Add(source, target);
                        vertices.Add(mesh.Vertices[source] with { LightmapUV = uv });
                    }
                    indices[t * 3 + c] = target;
                }
            }
        }
        return new(new(vertices.ToArray(), indices, mesh.Bounds), resolution, padding, allocations, triangleCharts);

        bool Planar(int t, Chart chart)
        {
            if (math.dot(chart.Normal, normals[t]) < 1 - 1e-10) return false;
            for (var c = 0; c < 3; c++)
                if (System.Math.Abs(math.dot(chart.Normal, positions[mesh.Indices[t * 3 + c]] - chart.Origin)) > tolerance)
                    return false;
            return true;
        }
    }

    private static void ConnectAttributeSeams(MeshData mesh, int[] adjacent,
        Dictionary<(uint, uint), (int Corner, int Second, uint From)> edges, CancellationToken cancellationToken)
    {
        // Canonicalize only adjacency identity, never vertices or shading attributes.
        // UV discontinuities remain chart boundaries. Exact equality avoids joining
        // nearby surfaces; an ambiguous physical edge is conservatively left split.
        var identities = new Dictionary<(float, float, float, float, float), uint>();
        var vertices = new uint[mesh.Vertices.Length];
        for (var i = 0; i < vertices.Length; i++) {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var v = mesh.Vertices[i];
            var key = (v.Position.x, v.Position.y, v.Position.z, v.UV.x, v.UV.y);
            if (!identities.TryGetValue(key, out var id)) {
                id = (uint)identities.Count;
                identities.Add(key, id);
            }
            vertices[i] = id;
        }
        // Reuse the validated index-edge table's capacity instead of retaining a
        // second full edge table during chart generation.
        edges.Clear();
        for (var corner = 0; corner < mesh.Indices.Length; corner++) {
            if ((corner & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var from = vertices[mesh.Indices[corner]];
            var to = vertices[mesh.Indices[corner / 3 * 3 + (corner + 1) % 3]];
            var key = from < to ? (from, to) : (to, from);
            if (!edges.TryGetValue(key, out var edge)) edges.Add(key, (corner, -1, from));
            else if (edge.Corner >= 0) {
                edges[key] = edge.Second < 0 && edge.From != from
                    ? edge with { Second = corner } : (-1, -1, 0);
            }
        }
        var visited = 0;
        foreach (var edge in edges.Values) {
            if ((visited++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (edge.Corner < 0 || edge.Second < 0 || adjacent[edge.Corner] >= 0 || adjacent[edge.Second] >= 0) continue;
            adjacent[edge.Corner] = edge.Second / 3;
            adjacent[edge.Second] = edge.Corner / 3;
        }
    }

    private sealed class Chart(double3 normal, double3 origin)
    {
        public readonly double3 Normal = normal;
        public readonly double3 Origin = origin;
        public readonly List<int> Triangles = [];
        private readonly int _axis = System.Math.Abs(normal.x) >= System.Math.Abs(normal.y)
            && System.Math.Abs(normal.x) >= System.Math.Abs(normal.z) ? 0
            : System.Math.Abs(normal.y) >= System.Math.Abs(normal.z) ? 1 : 2;
        public double2 Min, Size;
        public bool SourceCoordinates;
        public double UVSign;
        public double2 UVOrigin;
        public MeshLightmapChart Allocation;
        public bool TriangleBasis;
        private double3 _u, _v;

        public void AlignTriangle(uint[] indices, double3[] positions)
        {
            var t = Triangles[0];
            var a = positions[indices[t * 3]];
            var b = positions[indices[t * 3 + 1]];
            var c = positions[indices[t * 3 + 2]];
            var edge = b - a;
            if (math.length(c - b) > math.length(edge)) edge = c - b;
            if (math.length(a - c) > math.length(edge)) edge = a - c;
            _u = edge / math.length(edge);
            _v = math.cross(Normal, _u);
            TriangleBasis = true;
        }

        public double2 Project(uint index, double3[] positions, MeshVertex[] vertices)
        {
            if (SourceCoordinates) {
                var source = vertices[index].UV;
                var uvSource = new double2(source.x, source.y) - UVOrigin;
                uvSource.x *= UVSign;
                return uvSource;
            }
            var p = positions[index] - Origin;
            if (TriangleBasis) return new(math.dot(p, _u), math.dot(p, _v));
            var uv = _axis == 0 ? new double2(p.y, p.z) : _axis == 1 ? new double2(p.z, p.x) : new double2(p.x, p.y);
            if ((_axis == 0 ? Normal.x : _axis == 1 ? Normal.y : Normal.z) < 0) uv.x = -uv.x;
            return uv;
        }

        public void SetBounds(uint[] indices, double3[] positions, MeshVertex[] vertices)
        {
            Min = new(double.PositiveInfinity);
            var max = new double2(double.NegativeInfinity);
            foreach (var t in Triangles)
                for (var c = 0; c < 3; c++) {
                    var p = Project(indices[t * 3 + c], positions, vertices);
                    Min = math.min(Min, p);
                    max = math.max(max, p);
                }
            Size = max - Min;
        }
    }

    private static bool Pack(List<Chart> charts, int[] order, int resolution, int padding, double scale,
        CancellationToken cancellationToken)
    {
        int x = 0, y = 0, rowHeight = 0;
        foreach (var id in order) {
            cancellationToken.ThrowIfCancellationRequested();
            var chart = charts[id];
            var w = System.Math.Ceiling((System.Math.Ceiling(System.Math.Max(1, chart.Size.x * scale)) + 1 + 2 * padding) / 2) * 2;
            var h = System.Math.Ceiling((System.Math.Ceiling(System.Math.Max(1, chart.Size.y * scale)) + 1 + 2 * padding) / 2) * 2;
            if (w > resolution || h > resolution) return false;
            if (x + w > resolution) { x = 0; y += rowHeight; rowHeight = 0; }
            if (y + h > resolution) return false;
            chart.Allocation = new(x, y, (int)w, (int)h);
            x += (int)w;
            rowHeight = System.Math.Max(rowHeight, (int)h);
        }
        return true;
    }

    private readonly record struct Projected(double2 A, double2 B, double2 C)
    {
        public double2 Min => math.min(A, math.min(B, C));
        public double2 Max => math.max(A, math.max(B, C));
    }

    private static bool OverlapsOrExceedsWorkBudget(Chart chart, uint[] indices, double3[] positions, MeshVertex[] vertices,
        CancellationToken cancellationToken)
    {
        if (chart.Triangles.Count <= 1) return false;
        var triangles = chart.Triangles.Select(t => new Projected(chart.Project(indices[t * 3], positions, vertices),
            chart.Project(indices[t * 3 + 1], positions, vertices), chart.Project(indices[t * 3 + 2], positions, vertices))).ToArray();
        var cells = System.Math.Min(128, (int)System.Math.Ceiling(System.Math.Sqrt(triangles.Length)));
        var buckets = new List<int>?[cells * cells];
        var tested = new HashSet<long>();
        var entries = 0;
        for (var i = 0; i < triangles.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var tri = triangles[i];
            var min = (tri.Min - chart.Min) / chart.Size * cells;
            var max = (tri.Max - chart.Min) / chart.Size * cells;
            var x0 = System.Math.Clamp((int)min.x, 0, cells - 1);
            var x1 = System.Math.Clamp((int)max.x, 0, cells - 1);
            var y0 = System.Math.Clamp((int)min.y, 0, cells - 1);
            var y1 = System.Math.Clamp((int)max.y, 0, cells - 1);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++) {
                    if (++entries > 1_000_000) return true;
                    var bucket = buckets[y * cells + x] ??= [];
                    foreach (var j in bucket) {
                        var key = (long)j << 32 | (uint)i;
                        if (!tested.Add(key)) continue;
                        if (tested.Count > 1_000_000) return true;
                        if (InteriorOverlap(tri, triangles[j])) return true;
                    }
                    bucket.Add(i);
                }
        }
        return false;
    }

    private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

    private static bool InteriorOverlap(Projected a, Projected b)
    {
        static bool Separated(Projected a, Projected b)
        {
            Span<double2> vertices = stackalloc double2[] { a.A, a.B, a.C };
            for (var e = 0; e < 3; e++) {
                var p = vertices[e];
                var edge = vertices[(e + 1) % 3] - p;
                if (System.Math.Max(Cross(edge, b.A - p), System.Math.Max(Cross(edge, b.B - p),
                        Cross(edge, b.C - p))) <= 0) return true;
            }
            return false;
        }
        return !Separated(a, b) && !Separated(b, a);
    }
}
