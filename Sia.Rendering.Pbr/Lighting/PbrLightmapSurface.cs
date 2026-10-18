using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>
/// Offline receiver sampling over a borrowed immutable layout and one instance transform.
/// Owns only a bounded tile-to-primitive index. Callers own page buffers, baking and publication.
/// </summary>
public sealed class PbrLightmapSurface
{
    public const int MaximumTriangleReferences = 1_000_000;
    private readonly MeshBakeLayout _layout;
    private readonly float4x4 _transform;
    private readonly double3 _normal0, _normal1, _normal2;
    private readonly int[] _offsets, _triangles;
    private readonly int _tiles;
    public int Edge { get; }
    public int MaterialSlot { get; }
    public int Resolution => _layout.Resolution;
    public int ChartCount => _layout.Charts.Length;
    /// <summary>Owned array elements, excluding borrowed geometry and caller page buffers.</summary>
    public ulong PayloadBytes => ((ulong)_offsets.Length + (ulong)_triangles.Length) * sizeof(int);

    public PbrLightmapSurface(MeshBakeLayout layout, float4x4 transform, int materialSlot,
        ulong maximumWorkingBytes = 16ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();
        if (materialSlot < 0) throw new ArgumentOutOfRangeException(nameof(materialSlot));
        if (!PbrSceneData.ValidTransform(transform))
            throw new ArgumentException("Lightmap receivers require a finite positive affine transform.", nameof(transform));
        _layout = layout; _transform = transform; MaterialSlot = materialSlot;
        Edge = System.Math.Min(PbrLightmapPageData.MaximumEdge, layout.Resolution);
        _tiles = layout.Resolution / Edge;
        var cells = _tiles * _tiles;
        // Offsets plus construction cursors are admitted before either allocation.
        var workingBytes = ((ulong)cells * 2 + 1) * sizeof(int);
        if (workingBytes > maximumWorkingBytes)
            throw new ArgumentException("Receiver index exceeds known-array admission.", nameof(maximumWorkingBytes));
        _offsets = new int[cells + 1];
        var a = (double3)transform.c0.xyz; var b = (double3)transform.c1.xyz; var c = (double3)transform.c2.xyz;
        var determinant = math.dot(a, math.cross(b, c));
        if (!double.IsFinite(determinant) || determinant <= 0)
            throw new ArgumentException("Receiver has no positive affine inverse.", nameof(transform));
        _normal0 = math.cross(b, c) / determinant;
        _normal1 = math.cross(c, a) / determinant;
        _normal2 = math.cross(a, b) / determinant;
        var selected = 0; var references = 0;
        for (var triangle = 0; triangle < layout.TriangleCharts.Length; triangle++) {
            if ((triangle & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var chart = layout.TriangleCharts[triangle];
            if (chart < 0 || layout.Charts[chart].MaterialSlot != materialSlot) continue;
            selected++;
            var geometry = Geometry(triangle);
            if (!FiniteFloat(geometry.A) || !FiniteFloat(geometry.B) || !FiniteFloat(geometry.C))
                throw new ArgumentException("Receiver transform exceeds finite world-position bounds.", nameof(transform));
            var range = TileRange(triangle);
            var added = (range.MaxX - range.MinX + 1) * (range.MaxY - range.MinY + 1);
            if (added > MaximumTriangleReferences - references)
                throw new ArgumentException("Receiver index exceeds its primitive-reference limit.", nameof(layout));
            references += added;
            if (workingBytes + (ulong)references * sizeof(int) > maximumWorkingBytes)
                throw new ArgumentException("Receiver index exceeds known-array admission.", nameof(maximumWorkingBytes));
            for (var y = range.MinY; y <= range.MaxY; y++)
                for (var x = range.MinX; x <= range.MaxX; x++) _offsets[y * _tiles + x + 1]++;
        }
        if (selected == 0) throw new ArgumentException("Receiver slot has no nondegenerate chart primitive.", nameof(materialSlot));
        for (var cell = 1; cell < _offsets.Length; cell++) _offsets[cell] += _offsets[cell - 1];
        _triangles = new int[references];
        var cursors = _offsets.AsSpan(0, cells).ToArray();
        for (var triangle = 0; triangle < layout.TriangleCharts.Length; triangle++) {
            if ((triangle & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var chart = layout.TriangleCharts[triangle];
            if (chart < 0 || layout.Charts[chart].MaterialSlot != materialSlot) continue;
            var range = TileRange(triangle);
            for (var y = range.MinY; y <= range.MaxY; y++)
                for (var x = range.MinX; x <= range.MaxX; x++) _triangles[cursors[y * _tiles + x]++] = triangle;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Fills one fine page. Intersected pixels use a real barycentric surface point even when
    /// no pixel center lies in a thin triangle. AreaWeight sums the physical intersection area;
    /// the largest fragment supplies the representative shading sample. This is one-sample
    /// quadrature, not exact integration of lighting variation inside a pixel.
    /// Cancellation may leave the caller buffer partially written; discard it before retrying.
    /// </summary>
    public void FillPage(int pageX, int pageY, Span<PbrLightmapSurfaceTexel> output,
        CancellationToken cancellationToken = default)
    {
        if ((uint)pageX >= (uint)_tiles || (uint)pageY >= (uint)_tiles)
            throw new ArgumentOutOfRangeException(nameof(pageX), "Receiver page lies outside the layout.");
        if (output.Length != Edge * Edge) throw new ArgumentException("Receiver buffer must contain exactly one page.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        var cell = pageY * _tiles + pageX;
        var candidates = _triangles.AsSpan(_offsets[cell], _offsets[cell + 1] - _offsets[cell]);
        Span<double2> polygon = stackalloc double2[8];
        Span<double2> clipped = stackalloc double2[8];
        for (var row = 0; row < Edge; row++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < Edge; column++) {
                var x = pageX * Edge + column; var y = pageY * Edge + row;
                var center = new double2(x + .5, y + .5);
                var bestArea = 0d; var totalArea = 0d; var bestDistance = double.PositiveInfinity;
                PbrLightmapSurfaceTexel sample = default;
                for (var at = 0; at < candidates.Length; at++) {
                    if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var triangle = candidates[at]; var chartIndex = _layout.TriangleCharts[triangle];
                    var chart = _layout.Charts[chartIndex];
                    if (x < chart.X || y < chart.Y || x >= chart.X + chart.Width || y >= chart.Y + chart.Height) continue;
                    var uv = Coordinates(triangle);
                    var area = IntersectPixel(uv.A, uv.B, uv.C, x, y, polygon, clipped, out var point);
                    if (area > 0) {
                        var physicalArea = area * math.length(TriangleCross(triangle))
                            / Cross(uv.B - uv.A, uv.C - uv.A);
                        totalArea += physicalArea;
                        if (physicalArea > bestArea) {
                            bestArea = physicalArea;
                            sample = Sample(triangle, Barycentric(point, uv.A, uv.B, uv.C));
                        }
                    }
                    else if (bestArea == 0 && (x < chart.X + _layout.Padding || y < chart.Y + _layout.Padding
                        || x >= chart.X + chart.Width - _layout.Padding || y >= chart.Y + chart.Height - _layout.Padding)) {
                        // Dilation is restricted to this chart's filtering border, never an interior hole.
                        var nearest = ClosestPoint(center, uv.A, uv.B, uv.C);
                        var distance = math.lengthsq(nearest - center);
                        var radius = _layout.Padding + 1d;
                        if (distance < bestDistance && distance <= 2 * radius * radius) {
                            bestDistance = distance; sample = Sample(triangle, Barycentric(nearest, uv.A, uv.B, uv.C));
                        }
                    }
                }
                output[row * Edge + column] = sample with { AreaWeight = totalArea };
            }
        }
    }

    private (int MinX, int MinY, int MaxX, int MaxY) TileRange(int triangle)
    {
        var uv = Coordinates(triangle); var chart = _layout.Charts[_layout.TriangleCharts[triangle]];
        var low = math.min(uv.A, math.min(uv.B, uv.C)) - (_layout.Padding + 1);
        var high = math.max(uv.A, math.max(uv.B, uv.C)) + (_layout.Padding + 1);
        return (System.Math.Max(chart.X, (int)System.Math.Floor(low.x)) / Edge,
            System.Math.Max(chart.Y, (int)System.Math.Floor(low.y)) / Edge,
            System.Math.Min(chart.X + chart.Width - 1, (int)System.Math.Floor(high.x)) / Edge,
            System.Math.Min(chart.Y + chart.Height - 1, (int)System.Math.Floor(high.y)) / Edge);
    }

    private (double2 A, double2 B, double2 C) Coordinates(int triangle)
    {
        var source = _layout.Source; var at = triangle * 3;
        return ((double2)source.BakeUvs[(int)source.Indices[at]] * _layout.Resolution,
            (double2)source.BakeUvs[(int)source.Indices[at + 1]] * _layout.Resolution,
            (double2)source.BakeUvs[(int)source.Indices[at + 2]] * _layout.Resolution);
    }

    private (double3 A, double3 B, double3 C) Geometry(int triangle)
    {
        var source = _layout.Source; var at = triangle * 3;
        return (World(source.Vertices[(int)source.Indices[at]].Position),
            World(source.Vertices[(int)source.Indices[at + 1]].Position),
            World(source.Vertices[(int)source.Indices[at + 2]].Position));
    }

    private double3 World(float3 position) => (double3)_transform.c0.xyz * position.x
        + (double3)_transform.c1.xyz * position.y + (double3)_transform.c2.xyz * position.z + (double3)_transform.c3.xyz;

    private double3 TriangleCross(int triangle)
    {
        var source = _layout.Source; var at = triangle * 3;
        var a = (double3)source.Vertices[(int)source.Indices[at]].Position;
        var b = (double3)source.Vertices[(int)source.Indices[at + 1]].Position - a;
        var c = (double3)source.Vertices[(int)source.Indices[at + 2]].Position - a;
        double3 Vector(double3 v) => (double3)_transform.c0.xyz * v.x + (double3)_transform.c1.xyz * v.y + (double3)_transform.c2.xyz * v.z;
        return math.cross(Vector(b), Vector(c));
    }

    private PbrLightmapSurfaceTexel Sample(int triangle, double3 barycentric)
    {
        // Clip-rounding at an edge must not produce an off-surface representative.
        barycentric = math.max(barycentric, new double3(0)); barycentric /= barycentric.x + barycentric.y + barycentric.z;
        var source = _layout.Source; var at = triangle * 3;
        var geometry = Geometry(triangle);
        var local = (double3)source.Vertices[(int)source.Indices[at]].Normal * barycentric.x
            + (double3)source.Vertices[(int)source.Indices[at + 1]].Normal * barycentric.y
            + (double3)source.Vertices[(int)source.Indices[at + 2]].Normal * barycentric.z;
        var normal = _normal0 * local.x + _normal1 * local.y + _normal2 * local.z;
        if (math.all(normal == 0)) normal = TriangleCross(triangle);
        normal /= math.length(normal);
        return new((float3)(geometry.A * barycentric.x + geometry.B * barycentric.y + geometry.C * barycentric.z), (float3)normal, true) {
            Chart = _layout.TriangleCharts[triangle], SourceTriangle = triangle
        };
    }

    private static bool FiniteFloat(double3 value) => math.all(math.isfinite(value)) && math.all(math.abs(value) <= float.MaxValue);
    private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;
    private static double3 Barycentric(double2 p, double2 a, double2 b, double2 c)
    {
        var divisor = Cross(b - a, c - a);
        var y = Cross(p - a, c - a) / divisor; var z = Cross(b - a, p - a) / divisor;
        return new(1 - y - z, y, z);
    }

    private static double2 ClosestPoint(double2 p, double2 a, double2 b, double2 c)
    {
        var first = Segment(p, a, b); var second = Segment(p, b, c); var third = Segment(p, c, a);
        if (math.lengthsq(second - p) < math.lengthsq(first - p)) first = second;
        return math.lengthsq(third - p) < math.lengthsq(first - p) ? third : first;
    }

    private static double2 Segment(double2 p, double2 a, double2 b)
    {
        var edge = b - a;
        return a + edge * System.Math.Clamp(math.dot(p - a, edge) / math.lengthsq(edge), 0, 1);
    }

    private static double IntersectPixel(double2 a, double2 b, double2 c, int x, int y,
        Span<double2> polygon, Span<double2> clipped, out double2 centroid)
    {
        // Pixel-relative coordinates avoid subtracting large nearly equal area/centroid sums.
        var origin = new double2(x, y);
        polygon[0] = a - origin; polygon[1] = b - origin; polygon[2] = c - origin;
        var count = 3;
        for (var plane = 0; plane < 4 && count > 0; plane++) {
            var next = 0; var previous = polygon[count - 1]; var distance = Distance(previous, plane);
            for (var at = 0; at < count; at++) {
                var point = polygon[at]; var current = Distance(point, plane);
                if ((distance >= 0) != (current >= 0)) clipped[next++] = previous + (point - previous) * (distance / (distance - current));
                if (current >= 0) clipped[next++] = point;
                previous = point; distance = current;
            }
            clipped[..next].CopyTo(polygon); count = next;
        }
        var twiceArea = 0d; var weighted = new double2(0);
        for (var at = 0; at < count; at++) {
            var point = polygon[at]; var next = polygon[(at + 1) % count]; var cross = Cross(point, next);
            twiceArea += cross; weighted += (point + next) * cross;
        }
        centroid = twiceArea > 0 ? origin + weighted / (3 * twiceArea) : origin;
        return System.Math.Max(0, twiceArea * .5);
    }

    private static double Distance(double2 point, int plane) => plane switch {
        0 => point.x, 1 => 1 - point.x, 2 => point.y, _ => 1 - point.y
    };
}
