using Sia.Math;

namespace Sia.Engine.Rendering;

public static partial class IblEnvironmentBaker
{
    /// <summary>Principal scratch and final owned cube bytes, excluding array headers and caller callback storage.</summary>
    public static ulong RadianceWorkingBytes => 6ul * IblEnvironmentAsset.CubeSize * IblEnvironmentAsset.CubeSize
        * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<float3>() + IblEnvironmentAsset.CubeTexels * 16ul;

    /// <summary>
    /// Pure CPU producer: invokes the borrowed scene callback exactly once per base texel, then filters
    /// the cached radiance using GGX importance sampling. Preserves shared sky/SH/BRDF. No IO or GPU effects.
    /// Failure/cancellation publishes no output. Admission includes scratch and the final owned cube.
    /// </summary>
    public static IblEnvironmentAsset BakeRadiance(IblEnvironmentAsset source, Func<float3, float3, float3> sample,
        int filterSamples = 64, ulong maximumWorkingBytes = 4ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        if (filterSamples is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(filterSamples));
        const int size = IblEnvironmentAsset.CubeSize, baseCount = 6 * size * size;
        if (RadianceWorkingBytes > maximumWorkingBytes) throw new ArgumentException("Capture scratch and final cube exceed the working budget.", nameof(maximumWorkingBytes));
        var radiance = new float3[baseCount]; var cube = new Half[IblEnvironmentAsset.CubeTexels * 4];
        var peak = 0f;
        for (var face = 0; face < 6; face++) for (var y = 0; y < size; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++) {
                var at = (face * size + y) * size + x; var offset = at * 4;
                var background = new float3((float)source.Cube.Span[offset], (float)source.Cube.Span[offset + 1], (float)source.Cube.Span[offset + 2]) * source.CubeRadianceScale;
                var value = sample(CubeDirection(face, x, y, size), background);
                if (!math.all(math.isfinite(value)) || math.any(value < 0) || math.any(value > 1e18f))
                    throw new InvalidDataException("Capture radiance must be nonnegative, finite and within the transport range.");
                radiance[at] = value; peak = MathF.Max(peak, MathF.Max(value.x, MathF.Max(value.y, value.z)));
            }
        }
        var scale = 1f;
        while (peak / scale > 65504) scale *= 2;
        for (var at = 0; at < baseCount; at++) {
            if ((at & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            Store(cube, at, radiance[at] / scale);
        }
        var start = baseCount;
        for (var mip = 1; mip < IblEnvironmentAsset.MipCount; mip++) {
            var edge = size >> mip; var roughness = (float)mip / (IblEnvironmentAsset.MipCount - 1);
            for (var face = 0; face < 6; face++) for (var y = 0; y < edge; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < edge; x++) {
                    var n = CubeDirection(face, x, y, edge); var up = MathF.Abs(n.z) < .999f ? new float3(0, 0, 1) : new(1, 0, 0);
                    var tangent = math.normalize(math.cross(up, n)); var bitangent = math.cross(n, tangent);
                    var total = float3.zero; var weight = 0f;
                    for (uint index = 0; index < filterSamples; index++) {
                        var u = (float)index / filterSamples; var v = RadicalInverse(index);
                        var alpha = roughness * roughness; var cosine = MathF.Sqrt((1 - v) / (1 + (alpha * alpha - 1) * v));
                        var sine = MathF.Sqrt(MathF.Max(0, 1 - cosine * cosine)); var angle = 2 * MathF.PI * u;
                        var half = tangent * (MathF.Cos(angle) * sine) + bitangent * (MathF.Sin(angle) * sine) + n * cosine;
                        var light = 2 * math.dot(n, half) * half - n; var nl = MathF.Max(0, math.dot(n, light));
                        if (nl <= 0) continue;
                        total += SampleBase(radiance, light, size) * nl; weight += nl;
                    }
                    if (weight <= 0) throw new InvalidDataException("Capture filter has no supported direction.");
                    Store(cube, start + (face * edge + y) * edge + x, total / (weight * scale));
                }
            }
            start += 6 * edge * edge;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return source.WithCube(cube, cancellationToken: cancellationToken, cubeRadianceScale: scale);
    }

    private static void Store(Half[] output, int texel, float3 value)
    { var at = texel * 4; output[at] = (Half)value.x; output[at + 1] = (Half)value.y; output[at + 2] = (Half)value.z; output[at + 3] = (Half)1; }

    private static float RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16); bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8); return bits * 2.3283064365386963e-10f;
    }

    private static float3 CubeDirection(int face, int x, int y, int size)
    {
        var u = (x + .5f) * 2 / size - 1; var v = (y + .5f) * 2 / size - 1;
        return math.normalize(face switch { 0 => new float3(1, -v, -u), 1 => new(-1, -v, u), 2 => new(u, 1, v),
            3 => new(u, -1, -v), 4 => new(u, -v, 1), _ => new(-u, -v, -1) });
    }

    private static (int Face, float X, float Y) Project(float3 direction, int size)
    {
        var a = math.abs(direction); int face; float u, v;
        if (a.x >= a.y && a.x >= a.z) { face = direction.x >= 0 ? 0 : 1; u = (face == 0 ? -direction.z : direction.z) / a.x; v = -direction.y / a.x; }
        else if (a.y >= a.z) { face = direction.y >= 0 ? 2 : 3; u = direction.x / a.y; v = (face == 2 ? direction.z : -direction.z) / a.y; }
        else { face = direction.z >= 0 ? 4 : 5; u = (face == 4 ? direction.x : -direction.x) / a.z; v = -direction.y / a.z; }
        return (face, (u + 1) * size * .5f - .5f, (v + 1) * size * .5f - .5f);
    }

    private static float3 SampleBase(float3[] pixels, float3 direction, int size)
    {
        var p = Project(direction, size); var x = (int)MathF.Floor(p.X); var y = (int)MathF.Floor(p.Y); var tx = p.X - x; var ty = p.Y - y;
        float3 Fetch(int ix, int iy) {
            if (ix >= 0 && ix < size && iy >= 0 && iy < size) return pixels[(p.Face * size + iy) * size + ix];
            // Fold border taps onto the adjacent face instead of clamping every seam.
            var neighbor = Project(CubeDirection(p.Face, ix, iy, size), size);
            var nx = System.Math.Clamp((int)MathF.Round(neighbor.X), 0, size - 1); var ny = System.Math.Clamp((int)MathF.Round(neighbor.Y), 0, size - 1);
            return pixels[(neighbor.Face * size + ny) * size + nx];
        }
        return math.lerp(math.lerp(Fetch(x, y), Fetch(x + 1, y), tx), math.lerp(Fetch(x, y + 1), Fetch(x + 1, y + 1), tx), ty);
    }
}
