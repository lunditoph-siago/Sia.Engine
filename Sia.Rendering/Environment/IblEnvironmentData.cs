using System.Buffers.Binary;
using System.Diagnostics;
using Sia.Math;

namespace Sia.Engine.Rendering;

internal static class IblEnvironmentData
{
    private const uint Samples = 256;
    private static readonly Lock s_gate = new();
    private static readonly Lazy<Half[]> s_brdfLut = new(ReadBrdfLut);
    private static IblEnvironmentAsset? s_recent, s_previous;
    private static long s_hits, s_generated;
    private static double s_generationMilliseconds;

    internal static ReadOnlySpan<Half> BrdfLut => s_brdfLut.Value;

    internal readonly record struct PreparationStatistics(int CachedSkies, long SharedCpuBytes,
        long CacheHits, long GeneratedSkies, double GenerationMilliseconds);

    internal static PreparationStatistics Statistics {
        get {
            lock (s_gate) {
                var count = (s_recent is null ? 0 : 1) + (s_previous is null ? 0 : 1);
                var bytes = count * (9L * 16 + IblEnvironmentAsset.CubeTexels * 8L)
                    + (s_brdfLut.IsValueCreated ? IblEnvironmentAsset.LutTexels * 8L : 0);
                return new(count, bytes, s_hits, s_generated, s_generationMilliseconds);
            }
        }
    }

    // Two immutable CPU payloads retain two 1 MiB cubes plus one shared 128 KiB
    // LUT. GPU allocations and asset entity ownership remain per renderer.
    internal static IblEnvironmentAsset Get(ProceduralSky sky)
    {
        ArgumentNullException.ThrowIfNull(sky);
        sky.ValidateData();
        lock (s_gate) {
            if (s_recent is { } recent && recent.Sky == sky) {
                s_hits++;
                return recent;
            }
            if (s_previous is { } previous && previous.Sky == sky) {
                (s_recent, s_previous) = (s_previous, s_recent);
                s_hits++;
                return previous;
            }
            var start = Stopwatch.GetTimestamp();
            var result = Create(sky);
            s_previous = s_recent;
            s_recent = result;
            s_generated++;
            s_generationMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return result;
        }
    }

    private static IblEnvironmentAsset Create(ProceduralSky sky)
    {
        var scale = RadianceScale(sky);
        var sun = math.normalize(sky.SunDirection);
        var coefficients = IrradianceSh.Project(direction => sky.Evaluate(direction, sun));
        var cube = new Half[IblEnvironmentAsset.CubeTexels * 4];
        var offset = 0;
        Span<float3> samples = stackalloc float3[(int)Samples];
        for (var mip = 0; mip < IblEnvironmentAsset.MipCount; mip++) {
            var size = IblEnvironmentAsset.CubeSize >> mip;
            var roughness = (float)mip / (IblEnvironmentAsset.MipCount - 1);
            var count = mip == 0 ? 1u : Samples;
            for (uint sample = 0; sample < count; sample++) samples[(int)sample] = SampleCoordinates(sample, roughness);
            for (var face = 0; face < 6; face++) {
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++) {
                        var u = ((x + .5f) / size * 2) - 1;
                        var v = ((y + .5f) / size * 2) - 1;
                        var n = math.normalize(face switch {
                            0 => new float3(1, -v, -u),
                            1 => new(-1, -v, u),
                            2 => new(u, 1, v),
                            3 => new(u, -1, -v),
                            4 => new(u, -v, 1),
                            _ => new(-u, -v, -1)
                        });
                        var tangent = math.normalize(math.cross(MathF.Abs(n.z) < .999f ? new float3(0, 0, 1) : new(1, 0, 0), n));
                        var bitangent = math.cross(n, tangent);
                        var color = float3.zero;
                        float weight = 0;
                        for (var sample = 0; sample < count; sample++) {
                            var coordinates = samples[sample];
                            var h = math.normalize((tangent * coordinates.x) + (bitangent * coordinates.y) + (n * coordinates.z));
                            var l = (2 * math.dot(n, h) * h) - n;
                            var nl = MathF.Max(0, math.dot(n, l));
                            color += sky.Evaluate(l, sun) * nl;
                            weight += nl;
                        }
                        color /= MathF.Max(weight, 1e-6f);
                        cube[offset++] = (Half)(color.x / scale);
                        cube[offset++] = (Half)(color.y / scale);
                        cube[offset++] = (Half)(color.z / scale);
                        cube[offset++] = (Half)1;
                    }
            }
        }
        return new(sky, coefficients, cube, s_brdfLut.Value, scale);
    }

    internal static double MaximumRadiance(ProceduralSky sky)
    {
        var unscaled = (double)math.cmax(math.max(sky.Horizon, math.max(sky.Zenith, sky.Ground))) + math.cmax(sky.SunRadiance);
        var peak = unscaled * sky.Intensity;
        if (unscaled > float.MaxValue || !double.IsFinite(peak) || peak > float.MaxValue / 4096d)
            throw new ArgumentException("Procedural environment exceeds finite Float32 projection admission.", nameof(sky));
        return peak;
    }

    private static float RadianceScale(ProceduralSky sky)
    {
        var peak = MaximumRadiance(sky);
        var scale = 1f;
        while (peak / scale > 32752) {
            if (scale >= MathF.ScaleB(1, 100)) throw new ArgumentException("Procedural environment exceeds the cube radiance scale range.", nameof(sky));
            scale *= 2;
        }
        return scale;
    }

    // BrdfLut.bin contains little-endian Half values generated by this reference
    // integration at 128x128 and 256 samples. The regression compares every value.
    private static Half[] ReadBrdfLut()
    {
        using var input = typeof(IblEnvironmentData).Assembly.GetManifestResourceStream("Sia.Engine.Rendering.BrdfLut.bin")
            ?? throw new InvalidOperationException("Missing BRDF integration table.");
        var bytes = new byte[IblEnvironmentAsset.LutTexels * 4 * sizeof(ushort)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Invalid BRDF integration table length.");
        var result = new Half[IblEnvironmentAsset.LutTexels * 4];
        for (var i = 0; i < result.Length; i++)
            result[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort))));
        return result;
    }

    internal static Half[] GenerateBrdfLut()
    {
        var lut = new Half[IblEnvironmentAsset.LutTexels * 4];
        for (var y = 0; y < IblEnvironmentAsset.LutSize; y++)
            for (var x = 0; x < IblEnvironmentAsset.LutSize; x++) {
                var nv = (x + .5f) / IblEnvironmentAsset.LutSize;
                var roughness = (y + .5f) / IblEnvironmentAsset.LutSize;
                var v = new float3(MathF.Sqrt(1 - (nv * nv)), 0, nv);
                float scale = 0, bias = 0;
                for (uint i = 0; i < Samples; i++) {
                    var h = Sample(i, roughness, new(0, 0, 1));
                    var vh = MathF.Max(0, math.dot(v, h));
                    var l = (2 * vh * h) - v;
                    var nl = MathF.Max(0, l.z);
                    if (nl == 0) continue;
                    var k = roughness * roughness * .5f;
                    var g = nv / ((nv * (1 - k)) + k) * nl / ((nl * (1 - k)) + k);
                    var visibility = g * vh / MathF.Max(h.z * nv, 1e-4f);
                    var f = MathF.Pow(1 - vh, 5);
                    scale += (1 - f) * visibility;
                    bias += f * visibility;
                }
                var offset = ((y * IblEnvironmentAsset.LutSize) + x) * 4;
                lut[offset] = (Half)(scale / Samples);
                lut[offset + 1] = (Half)(bias / Samples);
                lut[offset + 3] = (Half)1;
            }
        return lut;
    }

    private static float3 Sample(uint index, float roughness, float3 n)
    {
        var coordinates = SampleCoordinates(index, roughness);
        var t = math.normalize(math.cross(MathF.Abs(n.z) < .999f ? new float3(0, 0, 1) : new(1, 0, 0), n));
        return math.normalize((t * coordinates.x) + (math.cross(n, t) * coordinates.y) + (n * coordinates.z));
    }

    private static float3 SampleCoordinates(uint index, float roughness)
    {
        var bits = index;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555) << 1) | ((bits & 0xaaaaaaaa) >> 1);
        bits = ((bits & 0x33333333) << 2) | ((bits & 0xcccccccc) >> 2);
        bits = ((bits & 0x0f0f0f0f) << 4) | ((bits & 0xf0f0f0f0) >> 4);
        bits = ((bits & 0x00ff00ff) << 8) | ((bits & 0xff00ff00) >> 8);
        var y = bits * 2.3283064365386963e-10f;
        var a = roughness * roughness;
        var phi = MathF.Tau * index / Samples;
        var cosine = MathF.Sqrt((1 - y) / (1 + (((a * a) - 1) * y)));
        var sine = MathF.Sqrt(MathF.Max(0, 1 - (cosine * cosine)));
        return new(MathF.Cos(phi) * sine, MathF.Sin(phi) * sine, cosine);
    }
}
