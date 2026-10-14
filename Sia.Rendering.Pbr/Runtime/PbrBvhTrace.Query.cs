using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrBvhTrace
{
    /// <summary>
    /// Closest geometric hit in inclusive [minimum, maximum], testing both sides and all material kinds.
    /// A miss returns false/default. Exact ties choose stable instance Guid then original primitive ID.
    /// Optional resolved-material inclusion is applied during traversal, preserving coincident eligible hits.
    /// </summary>
    public bool TryTrace(float3 origin, float3 direction, out PbrBvhHit hit, double minimum = 0,
        double maximum = double.PositiveInfinity, CancellationToken cancellationToken = default,
        ReadOnlySpan<bool> includedMaterials = default)
    {
        if (!math.all(math.isfinite(origin)) || !math.all(math.isfinite(direction)) || math.all(direction == 0))
            throw new ArgumentException("Ray origin/direction must be finite and direction nonzero.");
        if (!double.IsFinite(minimum) || minimum < 0 || double.IsNaN(maximum) || maximum < minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Ray range must satisfy 0 <= minimum <= maximum.");
        if (!includedMaterials.IsEmpty && includedMaterials.Length != Scene.Materials.Length)
            throw new ArgumentException("Material inclusion must cover every resolved scene material.", nameof(includedMaterials));
        cancellationToken.ThrowIfCancellationRequested();
        hit = default; var found = false; var best = maximum;
        var o = (double3)origin; var d = (double3)direction;
        for (var nodeIndex = 0; nodeIndex < _nodes.Length;) {
            cancellationToken.ThrowIfCancellationRequested();
            var node = _nodes[nodeIndex];
            if (!Intersects(node.Bounds, o, d, minimum, best)) { nodeIndex = node.Escape; continue; }
            for (var at = node.First; at < node.First + node.Count; at++)
                TraceInstance(_order[at], o, d, minimum, ref best, ref found, ref hit, cancellationToken, includedMaterials);
            nodeIndex++;
        }
        return found;
    }

    private void TraceInstance(int index, double3 origin, double3 direction, double minimum,
        ref double best, ref bool found, ref PbrBvhHit hit, CancellationToken cancellationToken, ReadOnlySpan<bool> includedMaterials)
    {
        var transform = _instances[index];
        if (transform.Disabled) return;
        if (!Intersects(transform.Bounds, origin, direction, minimum, best)) return;
        var localOrigin = transform.LocalVector(origin - transform.Translation);
        var localDirection = transform.LocalVector(direction); // Keep world ray parameter under nonuniform scale/shear.
        var instance = Scene.Instances[index]; var binding = Scene.Bindings[instance.Binding];
        var mesh = Scene.Geometry[binding.Geometry];
        for (var nodeIndex = 0; nodeIndex < mesh.Nodes.Length;) {
            cancellationToken.ThrowIfCancellationRequested();
            var node = mesh.Nodes[nodeIndex];
            if (!Intersects(node.Bounds, localOrigin, localDirection, minimum, best)) { nodeIndex = node.EscapeIndex; continue; }
            for (var at = node.FirstTriangle; at < node.FirstTriangle + node.TriangleCount; at++) {
                var triangle = mesh.Triangles[at];
                var material = includedMaterials.IsEmpty ? -1 : binding.GetMaterial(triangle.MaterialSlot);
                if (material >= 0 && !includedMaterials[material]) continue;
                if (!IntersectTriangle(mesh, triangle, localOrigin, localDirection, out var parameter, out var barycentric, out var front)
                    || parameter < minimum || parameter > best) continue;
                if (found && parameter == best) {
                    var idOrder = instance.Id.CompareTo(hit.InstanceId);
                    if (idOrder > 0 || idOrder == 0 && triangle.SourceTriangle >= hit.SourceTriangle) continue;
                }
                best = parameter; found = true;
                hit = new(instance.Id, index, triangle.SourceTriangle, triangle.MaterialSlot,
                    material >= 0 ? material : binding.GetMaterial(triangle.MaterialSlot), parameter, barycentric, front) {
                    GeometricNormal = GeometricNormal(mesh, triangle, transform)
                };
            }
            nodeIndex++;
        }
    }

    private static float3 GeometricNormal(MeshBvhData mesh, MeshBvhTriangle triangle, InstanceData transform)
    {
        var a = (double3)mesh.Positions[(int)triangle.A];
        var normal = math.cross((double3)mesh.Positions[(int)triangle.B] - a, (double3)mesh.Positions[(int)triangle.C] - a);
        // Inverse transpose preserves the authored orientation under positive affine transforms.
        var world = transform.Row0 * normal.x + transform.Row1 * normal.y + transform.Row2 * normal.z;
        var scale = System.Math.Max(System.Math.Abs(world.x), System.Math.Max(System.Math.Abs(world.y), System.Math.Abs(world.z)));
        world /= scale;
        return (float3)(world / math.length(world));
    }

    private static bool Intersects(Aabb bounds, double3 origin, double3 direction, double minimum, double maximum)
    {
        for (var axis = 0; axis < 3; axis++) {
            var lo = (double)bounds.Min[axis]; var hi = (double)bounds.Max[axis];
            if (direction[axis] == 0) { if (origin[axis] < lo || origin[axis] > hi) return false; continue; }
            var a = (lo - origin[axis]) / direction[axis]; var b = (hi - origin[axis]) / direction[axis];
            minimum = System.Math.Max(minimum, System.Math.Min(a, b)); maximum = System.Math.Min(maximum, System.Math.Max(a, b));
            if (minimum > maximum) return false;
        }
        return true;
    }

    private static bool IntersectTriangle(MeshBvhData mesh, MeshBvhTriangle triangle, double3 origin, double3 direction,
        out double parameter, out float3 barycentric, out bool front)
    {
        parameter = 0; barycentric = default; front = false;
        var positionA = (double3)mesh.Positions[(int)triangle.A];
        var positionB = (double3)mesh.Positions[(int)triangle.B];
        var positionC = (double3)mesh.Positions[(int)triangle.C];
        // Projection rounding can turn an exactly collinear primitive into a tiny apparent surface.
        if (math.all(math.cross(positionB - positionA, positionC - positionA) == 0)) return false;
        // Dominant-axis projected edge tests; Woop/Benthin/Wald, JCGT 2(1), 2013.
        // Double intermediates here are a CPU query implementation, not a formal all-input watertightness guarantee.
        var axis = math.abs(direction); var z = axis.y > axis.x ? 1 : 0; if (axis.z > axis[z]) z = 2;
        var x = (z + 1) % 3; var y = (x + 1) % 3; if (direction[z] < 0) (x, y) = (y, x);
        var sx = direction[x] / direction[z]; var sy = direction[y] / direction[z];
        var a = positionA - origin; var b = positionB - origin; var c = positionC - origin;
        var ax = a[x] - sx * a[z]; var ay = a[y] - sy * a[z];
        var bx = b[x] - sx * b[z]; var by = b[y] - sy * b[z];
        var cx = c[x] - sx * c[z]; var cy = c[y] - sy * c[z];
        var u = cx * by - cy * bx; var v = ax * cy - ay * cx; var w = bx * ay - by * ax;
        if ((u < 0 || v < 0 || w < 0) && (u > 0 || v > 0 || w > 0)) return false;
        var determinant = u + v + w; if (determinant == 0 || !double.IsFinite(determinant)) return false;
        u /= determinant; v /= determinant; w /= determinant;
        parameter = (u * a[z] + v * b[z] + w * c[z]) / direction[z];
        if (!double.IsFinite(parameter)) return false;
        barycentric = new((float)u, (float)v, (float)w); front = determinant > 0;
        return true;
    }
}
