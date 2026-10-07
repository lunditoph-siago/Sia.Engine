using Sia.Math;

namespace Sia.Engine.Rendering;

public static partial class IblEnvironmentBaker
{
    /// <summary>Offline radiance capture. Samples each base texel once, then filters the captured field.
    /// The callback must support concurrent calls; the supplied environment retains diffuse SH and BRDF data.</summary>
    public static IblEnvironmentAsset BakeRadiance(IblEnvironmentAsset environment, Func<float3, float3, float3> radiance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(radiance);
        const int size = IblEnvironmentAsset.CubeSize;
        var captured = new float3[6 * size * size];
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, captured.Length, options, index => {
            var pixel = index % (size * size);
            var direction = CubeDirection(index / (size * size),
                ((pixel % size + .5f) / size * 2) - 1, ((pixel / size + .5f) / size * 2) - 1);
            var background = environment.Cube.Span;
            var value = radiance(direction, new float3((float)background[index * 4],
                (float)background[index * 4 + 1], (float)background[index * 4 + 2]));
            if (!math.all(math.isfinite(value) & (value >= 0)))
                throw new InvalidDataException("Capture returned invalid radiance.");
            captured[index] = math.min(value, new float3(65504));
        });
        var cube = new Half[IblEnvironmentAsset.CubeTexels * 4];
        var start = 0;
        for (var mip = 0; mip < IblEnvironmentAsset.MipCount; mip++) {
            cancellationToken.ThrowIfCancellationRequested();
            var side = size >> mip;
            var offset = start;
            var roughness = (float)mip / (IblEnvironmentAsset.MipCount - 1);
            Parallel.For(0, 6 * side * side, options, index => {
                var color = captured[0];
                if (side == size) color = captured[index];
                else {
                    var pixel = index % (side * side);
                    var normal = CubeDirection(index / (side * side),
                        ((pixel % side + .5f) / side * 2) - 1, ((pixel / side + .5f) / side * 2) - 1);
                    color = float3.zero;
                    float weight = 0;
                    for (uint sample = 0; sample < 256; sample++) {
                        var half = IblEnvironmentGpu.Sample(sample, roughness, normal);
                        var light = (2 * math.dot(normal, half) * half) - normal;
                        var cosine = MathF.Max(0, math.dot(normal, light));
                        color += SampleCube(captured, light) * cosine;
                        weight += cosine;
                    }
                    color /= MathF.Max(weight, 1e-6f);
                }
                var target = (offset + index) * 4;
                cube[target] = (Half)MathF.Min(color.x, 65504);
                cube[target + 1] = (Half)MathF.Min(color.y, 65504);
                cube[target + 2] = (Half)MathF.Min(color.z, 65504);
                cube[target + 3] = (Half)1;
            });
            start += 6 * side * side;
        }
        return new(environment.Sky, environment.Coefficients.ToArray(), cube, environment.BrdfLut.ToArray());
    }

    private static float3 CubeDirection(int face, float u, float v) => math.normalize(face switch {
        0 => new float3(1, -v, -u), 1 => new(-1, -v, u), 2 => new(u, 1, v),
        3 => new(u, -1, -v), 4 => new(u, -v, 1), _ => new(-u, -v, -1)
    });

    private static float3 SampleCube(float3[] values, float3 d)
    {
        const int size = IblEnvironmentAsset.CubeSize;
        var a = math.abs(d);
        int face;
        float u, v;
        if (a.x >= a.y && a.x >= a.z) {
            face = d.x >= 0 ? 0 : 1; u = (d.x >= 0 ? -d.z : d.z) / a.x; v = -d.y / a.x;
        }
        else if (a.y >= a.z) {
            face = d.y >= 0 ? 2 : 3; u = d.x / a.y; v = (d.y >= 0 ? d.z : -d.z) / a.y;
        }
        else {
            face = d.z >= 0 ? 4 : 5; u = (d.z >= 0 ? d.x : -d.x) / a.z; v = -d.y / a.z;
        }
        var x = (u + 1) * .5f * size - .5f;
        var y = (v + 1) * .5f * size - .5f;
        var ix = (int)MathF.Floor(x); var iy = (int)MathF.Floor(y);
        // Reproject edge taps onto the neighboring face instead of clamping seams.
        float3 Tap(int tx, int ty) {
            if ((uint)tx < size && (uint)ty < size) return values[(face * size + ty) * size + tx];
            var direction = CubeDirection(face, (tx + .5f) / size * 2 - 1, (ty + .5f) / size * 2 - 1);
            return NearestCube(values, direction);
        }
        return math.lerp(math.lerp(Tap(ix, iy), Tap(ix + 1, iy), x - ix),
            math.lerp(Tap(ix, iy + 1), Tap(ix + 1, iy + 1), x - ix), y - iy);
    }

    private static float3 NearestCube(float3[] values, float3 d)
    {
        // Edge directions lie strictly inside the adjacent face after reprojection.
        var a = math.abs(d);
        int face; float u, v;
        if (a.x >= a.y && a.x >= a.z) { face = d.x >= 0 ? 0 : 1; u = (d.x >= 0 ? -d.z : d.z) / a.x; v = -d.y / a.x; }
        else if (a.y >= a.z) { face = d.y >= 0 ? 2 : 3; u = d.x / a.y; v = (d.y >= 0 ? d.z : -d.z) / a.y; }
        else { face = d.z >= 0 ? 4 : 5; u = (d.z >= 0 ? d.x : -d.x) / a.z; v = -d.y / a.z; }
        const int size = IblEnvironmentAsset.CubeSize;
        var x = System.Math.Clamp((int)((u + 1) * .5f * size), 0, size - 1);
        var y = System.Math.Clamp((int)((v + 1) * .5f * size), 0, size - 1);
        return values[(face * size + y) * size + x];
    }
}
