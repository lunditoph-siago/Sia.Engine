using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class IblEnvironmentAsset
{
    /// <summary>
    /// Samples physical radiance from the base cube with seamless bilinear filtering.
    /// A corner tap averages the three incident face texels. Borrows immutable data; allocates nothing.
    /// Hardware corner/filter precision can differ; this is a deterministic offline sampling rule.
    /// </summary>
    public float3 SampleRadiance(float3 direction)
    {
        if (!math.all(math.isfinite(direction)) || math.cmax(math.abs(direction)) == 0)
            throw new ArgumentOutOfRangeException(nameof(direction), "A cube sample needs a finite nonzero direction.");
        var (face, uv) = CubeCoordinate(direction);
        var point = (uv + new float2(1)) * (CubeSize * .5f) - new float2(.5f);
        var x = (int)MathF.Floor(point.x); var y = (int)MathF.Floor(point.y);
        var fraction = point - new float2(x, y);
        var top = math.lerp(CubeTap(face, x, y), CubeTap(face, x + 1, y), fraction.x);
        var bottom = math.lerp(CubeTap(face, x, y + 1), CubeTap(face, x + 1, y + 1), fraction.x);
        return math.lerp(top, bottom, fraction.y) * CubeRadianceScale;
    }

    private float3 CubeTap(int face, int x, int y)
    {
        var outsideX = x < 0 || x >= CubeSize; var outsideY = y < 0 || y >= CubeSize;
        if (!outsideX && !outsideY) return CubeTexel(face, x, y);
        if (outsideX && outsideY) {
            var edgeX = System.Math.Clamp(x, 0, CubeSize - 1); var edgeY = System.Math.Clamp(y, 0, CubeSize - 1);
            return (CubeTexel(face, edgeX, edgeY) + CubeEdgeTap(face, x, edgeY) + CubeEdgeTap(face, edgeX, y)) / 3;
        }
        return CubeEdgeTap(face, x, y);
    }

    private float3 CubeEdgeTap(int face, int x, int y)
    {
        var u = (x + .5f) * (2f / CubeSize) - 1; var v = (y + .5f) * (2f / CubeSize) - 1;
        var direction = face switch {
            0 => new float3(1, -v, -u), 1 => new(-1, -v, u),
            2 => new(u, 1, v), 3 => new(u, -1, -v),
            4 => new(u, -v, 1), _ => new(-u, -v, -1)
        };
        var (neighbor, uv) = CubeCoordinate(direction);
        var point = (uv + new float2(1)) * (CubeSize * .5f);
        return CubeTexel(neighbor, System.Math.Clamp((int)point.x, 0, CubeSize - 1), System.Math.Clamp((int)point.y, 0, CubeSize - 1));
    }

    private float3 CubeTexel(int face, int x, int y)
    {
        var at = ((face * CubeSize + y) * CubeSize + x) * 4;
        var cube = Cube.Span;
        return new((float)cube[at], (float)cube[at + 1], (float)cube[at + 2]);
    }

    private static (int Face, float2 Uv) CubeCoordinate(float3 direction)
    {
        var a = math.abs(direction);
        if (a.x >= a.y && a.x >= a.z)
            return direction.x >= 0 ? (0, new(-direction.z / a.x, -direction.y / a.x)) : (1, new(direction.z / a.x, -direction.y / a.x));
        if (a.y >= a.z)
            return direction.y >= 0 ? (2, new(direction.x / a.y, direction.z / a.y)) : (3, new(direction.x / a.y, -direction.z / a.y));
        return direction.z >= 0 ? (4, new(direction.x / a.z, -direction.y / a.z)) : (5, new(-direction.x / a.z, -direction.y / a.z));
    }
}
