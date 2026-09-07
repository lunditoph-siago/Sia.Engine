using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    private const int k_CoordinateCount = 12;
    private const int k_QuadricSize = k_CoordinateCount * (k_CoordinateCount + 1) / 2;

    private static double[] BuildQuadrics(MeshData mesh, Triangle[] triangles, bool[] locked,
        MeshPatchBuildSettings settings, CancellationToken cancellationToken, out double[] coordinates)
    {
        var quadrics = new double[checked(mesh.Vertices.Length * k_QuadricSize)];
        coordinates = new double[checked(mesh.Vertices.Length * k_CoordinateCount)];
        var origin = new double3(mesh.Bounds.Min.x, mesh.Bounds.Min.y, mesh.Bounds.Min.z);
        var extent = new double3(mesh.Bounds.Max.x, mesh.Bounds.Max.y, mesh.Bounds.Max.z) - origin;
        var scale = System.Math.Max(extent.x, System.Math.Max(extent.y, extent.z));
        if (scale == 0) { scale = 1; }
        for (var v = 0; v < mesh.Vertices.Length; v++) {
            var vertex = mesh.Vertices[v];
            var p = (Position(vertex) - origin) / scale;
            var values = coordinates.AsSpan(v * k_CoordinateCount, k_CoordinateCount);
            values[0] = p.x; values[1] = p.y; values[2] = p.z;
            values[3] = vertex.Normal.x; values[4] = vertex.Normal.y; values[5] = vertex.Normal.z;
            values[6] = vertex.UV.x; values[7] = vertex.UV.y;
            values[8] = vertex.Tangent.x; values[9] = vertex.Tangent.y; values[10] = vertex.Tangent.z; values[11] = 1;
        }
        Span<double> plane = stackalloc double[k_CoordinateCount];
        for (var t = 0; t < triangles.Length; t++) {
            if ((t & 255) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            var face = triangles[t];
            var a = coordinates.AsSpan(face.A * k_CoordinateCount, k_CoordinateCount);
            var b = coordinates.AsSpan(face.B * k_CoordinateCount, k_CoordinateCount);
            var c = coordinates.AsSpan(face.C * k_CoordinateCount, k_CoordinateCount);
            var p = new double3(a[0], a[1], a[2]);
            var e1 = new double3(b[0], b[1], b[2]) - p;
            var e2 = new double3(c[0], c[1], c[2]) - p;
            var cross = math.cross(e1, e2);
            var areaSquared = math.lengthsq(cross);
            var area = System.Math.Sqrt(areaSquared);
            if (areaSquared == 0 || !double.IsFinite(area)) {
                locked[face.A] = true; locked[face.B] = true; locked[face.C] = true;
                continue;
            }
            var normal = cross / area;
            plane.Clear();
            plane[0] = normal.x; plane[1] = normal.y; plane[2] = normal.z; plane[11] = -math.dot(normal, p);
            AddPlane(quadrics, face, plane, area);
            var basis1 = math.cross(e2, cross) / areaSquared;
            var basis2 = math.cross(cross, e1) / areaSquared;
            for (var attribute = 3; attribute < 11; attribute++) {
                var weight = attribute is 6 or 7 ? settings.UVWeight : settings.NormalWeight;
                if (weight == 0) { continue; }
                var gradient = basis1 * (b[attribute] - a[attribute]) + basis2 * (c[attribute] - a[attribute]);
                plane.Clear();
                plane[0] = gradient.x; plane[1] = gradient.y; plane[2] = gradient.z;
                plane[attribute] = -1; plane[11] = a[attribute] - math.dot(gradient, p);
                AddPlane(quadrics, face, plane, area * weight * weight);
            }
        }
        return quadrics;
    }

    private static void AddPlane(double[] quadrics, Triangle face, ReadOnlySpan<double> plane, double weight)
    {
        var index = 0;
        for (var i = 0; i < k_CoordinateCount; i++) {
            for (var j = i; j < k_CoordinateCount; j++) {
                var value = plane[i] * plane[j] * weight;
                quadrics[face.A * k_QuadricSize + index] += value;
                quadrics[face.B * k_QuadricSize + index] += value;
                quadrics[face.C * k_QuadricSize + index] += value;
                index++;
            }
        }
    }

    private static double Evaluate(ReadOnlySpan<double> quadric, ReadOnlySpan<double> point)
    {
        var result = 0d;
        var index = 0;
        for (var i = 0; i < k_CoordinateCount; i++) {
            for (var j = i; j < k_CoordinateCount; j++) {
                result += quadric[index++] * point[i] * point[j] * (i == j ? 1 : 2);
            }
        }
        return result;
    }
}
