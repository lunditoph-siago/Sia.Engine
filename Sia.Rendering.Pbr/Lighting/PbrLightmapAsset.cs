using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public enum PbrLightmapEncoding { L1Half, L1Unorm8 }

/// <summary>World-space RGB L1 diffuse radiance; direct lights, receiver material and specular are separate.</summary>
public sealed partial class PbrLightmapAsset
{
    public const int CoefficientCount = 4;
    public const int TexelBytes = 32;
    public int Resolution { get; }
    public ReadOnlyMemory<PbrLightmapReceiver> Receivers { get; }
    public ReadOnlyMemory<PbrLightmapChart> Charts { get; }
    public int MipCount { get; }
    public ReadOnlyMemory<byte> SceneIdentity { get; }
    public ReadOnlyMemory<byte> SurfaceIdentity { get; }
    public ReadOnlyMemory<byte> BakeIdentity { get; }
    public PbrLightmapBakeSettings Settings { get; }
    public PbrLightmapEncoding Encoding { get; }
    public int BytesPerTexel => Encoding == PbrLightmapEncoding.L1Half ? TexelBytes : 16;
    public ulong TextureBytes {
        get {
            ulong texels = 0;
            for (var mip = 0; mip < MipCount; mip++) { var size = (uint)(Resolution >> mip); texels += (ulong)size * size; }
            return texels * (uint)BytesPerTexel;
        }
    }
    internal ReadOnlyMemory<Half> Data { get; }
    internal ReadOnlyMemory<byte> QuantizedData { get; }
    internal ReadOnlyMemory<float4> DecodeScales { get; }

    public PbrLightmapAsset(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> sceneIdentity, ReadOnlySpan<byte> surfaceIdentity, PbrLightmapBakeSettings settings,
        ReadOnlySpan<float4> coefficients, ReadOnlySpan<PbrLightmapChart> charts = default)
        : this(resolution, receivers, sceneIdentity, surfaceIdentity, settings, coefficients, null, charts) { }

    // The baker/decoder transfer their private Half array. No public mutable alias is exposed.
    internal static PbrLightmapAsset FromHalf(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> sceneIdentity, ReadOnlySpan<byte> surfaceIdentity, PbrLightmapBakeSettings settings, Half[] data,
        ReadOnlySpan<PbrLightmapChart> charts = default)
        => new(resolution, receivers, sceneIdentity, surfaceIdentity, settings, default, data, charts);

    private PbrLightmapAsset(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> sceneIdentity, ReadOnlySpan<byte> surfaceIdentity, PbrLightmapBakeSettings settings,
        ReadOnlySpan<float4> coefficients, Half[]? packed, ReadOnlySpan<PbrLightmapChart> charts)
        : this(resolution, receivers, sceneIdentity, surfaceIdentity, settings,
            packed is null ? coefficients.Length : packed.Length / 4, charts)
    {
        if (packed is not null && packed.Length % 4 != 0) throw new ArgumentException("Invalid Half coefficient length.");
        var data = packed ?? new Half[coefficients.Length * 4];
        for (var i = 0; i < data.Length / 4; i++) {
            var v = packed is null ? coefficients[i]
                : new float4((float)data[i * 4], (float)data[i * 4 + 1], (float)data[i * 4 + 2], (float)data[i * 4 + 3]);
            ValidateCoefficient(v, i % 4);
            if (packed is null) {
                data[i * 4] = (Half)v.x;
                data[i * 4 + 1] = (Half)v.y;
                data[i * 4 + 2] = (Half)v.z;
                data[i * 4 + 3] = (Half)v.w;
            }
        }
        Data = data;
    }

    private static void ValidateCoefficient(float4 v, int band)
    {
        if (!math.all(math.isfinite(v)) || math.any(math.abs(v) > new float4(65504))
            || (band == 0 ? v.w is not (0 or 1) || math.any(v.xyz < 0) : v.w != 0))
            throw new ArgumentException("Invalid or unrepresentable lightmap coefficients.");
    }

    private PbrLightmapAsset(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> sceneIdentity, ReadOnlySpan<byte> surfaceIdentity, PbrLightmapBakeSettings settings, int coefficientCount,
        ReadOnlySpan<PbrLightmapChart> charts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var count = (long)resolution * resolution * CoefficientCount;
        if (resolution is < 8 or > 8192 || coefficientCount != count)
            throw new ArgumentException("Invalid lightmap dimensions or coefficient count.");
        if (sceneIdentity.Length != 32 || surfaceIdentity.Length != 32)
            throw new ArgumentException("Lightmap identities require 32 bytes.");
        var ordered = ValidateReceivers(resolution, receivers);
        Resolution = resolution;
        Receivers = ordered;
        SceneIdentity = sceneIdentity.ToArray();
        SurfaceIdentity = surfaceIdentity.ToArray();
        Settings = settings;
        Charts = ValidateCharts(resolution, ordered, charts);
        MipCount = 1;
        foreach (var chart in Charts.Span) MipCount = System.Math.Max(MipCount, ChartMaximumMip(chart) + 1);
        BakeIdentity = ChartBakeIdentity(ComputeBakeIdentity(sceneIdentity, surfaceIdentity, settings), Charts.Span);
    }

    internal static PbrLightmapReceiver[] ValidateReceivers(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers)
    {
        if (receivers.IsEmpty || receivers.Length > 1_000_000) throw new ArgumentException("Invalid receiver count.");
        var ids = new HashSet<int>();
        var ordered = receivers.ToArray();
        foreach (var r in ordered) {
            if (r.StaticInstance < 0 || !ids.Add(r.StaticInstance) || r.Resolution < 8
                || r.X < 0 || r.Y < 0 || r.X > resolution - r.Resolution || r.Y > resolution - r.Resolution)
                throw new ArgumentException("Invalid lightmap receiver allocation.");
        }
        // Sweep only overlapping X allocations. Receiver count and atlas size remain bounded.
        var byX = ordered.OrderBy(r => r.X).ThenBy(r => r.Y).ToArray();
        for (var i = 0; i < byX.Length; i++)
            for (var j = i + 1; j < byX.Length && byX[j].X < byX[i].X + byX[i].Resolution; j++)
                if (byX[j].Y < byX[i].Y + byX[i].Resolution && byX[i].Y < byX[j].Y + byX[j].Resolution)
                    throw new ArgumentException("Lightmap receivers overlap.");
        for (var i = 0; i < ordered.Length; i++) {
            var r = ordered[i];
            var scale = (float)r.Resolution / resolution;
            ordered[i] = r with { SourceInstance = -1, ScaleBias = new(scale, scale, (float)r.X / resolution, (float)r.Y / resolution) };
        }
        return ordered;
    }

    public float3 Irradiance(int x, int y, float3 normal)
    {
        if ((uint)x >= Resolution || (uint)y >= Resolution || !math.all(math.isfinite(normal)) || math.lengthsq(normal) < 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(x));
        normal = math.normalize(normal);
        if (Encoding == PbrLightmapEncoding.L1Unorm8) return QuantizedIrradiance(x, y, normal);
        var offset = (y * Resolution + x) * 16;
        var data = Data.Span;
        if (data[offset + 3] == (Half)0) return default;
        float3 Coefficient(int c) {
            var at = offset + c * 4;
            var values = Data.Span;
            return new((float)values[at], (float)values[at + 1], (float)values[at + 2]);
        }
        return math.max(Coefficient(0) * (.2820948f * MathF.PI)
            + (Coefficient(1) * normal.y + Coefficient(2) * normal.z + Coefficient(3) * normal.x)
                * (.4886025f * (2 * MathF.PI / 3)), float3.zero);
    }

    /// <summary>Canonical static domain plus full referenced derived mesh payloads, excluding dynamic table offsets.</summary>
    public static byte[] Identity(PbrSceneAsset scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALMSCENE1"u8);
        hash.AppendData(PbrSceneTransport.StaticIdentity(scene));
        var seen = new HashSet<int>();
        foreach (var instance in scene.Instances.Span) {
            if (instance.Dynamic || !seen.Add(instance.Geometry)) continue;
            hash.AppendData(SHA256.HashData(scene.Geometry.Span[instance.Geometry].Encode()));
        }
        return hash.GetHashAndReset();
    }

    internal static byte[] ComputeBakeIdentity(ReadOnlySpan<byte> scene, ReadOnlySpan<byte> surface, PbrLightmapBakeSettings settings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write("SIALMBAKE-L1-ONEBOUNCE1"u8);
        writer.Write(scene);
        writer.Write(surface);
        WriteSettings(writer, settings);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(!Charts.IsEmpty ? "SIALMAP3"u8 : Encoding == PbrLightmapEncoding.L1Half ? "SIALMAP1"u8 : "SIALMAP2"u8);
        if (!Charts.IsEmpty) { writer.Write((int)Encoding); writer.Write(Charts.Length); }
        writer.Write(Resolution);
        writer.Write(Receivers.Length);
        writer.Write(SceneIdentity.Span);
        writer.Write(SurfaceIdentity.Span);
        writer.Write(BakeIdentity.Span);
        WriteSettings(writer, Settings);
        for (var i = 0; i < Receivers.Length; i++) {
            var r = Receivers.Span[i];
            writer.Write(r.StaticInstance); writer.Write(r.X); writer.Write(r.Y); writer.Write(r.Resolution);
            if (Encoding == PbrLightmapEncoding.L1Unorm8)
                foreach (var scale in DecodeScales.Span.Slice(i * 4, 4)) {
                    writer.Write(scale.x); writer.Write(scale.y); writer.Write(scale.z); writer.Write(scale.w);
                }
        }
        foreach (var chart in Charts.Span) {
            writer.Write(chart.StaticInstance); writer.Write(chart.X); writer.Write(chart.Y); writer.Write(chart.Width); writer.Write(chart.Height);
        }
        if (Encoding == PbrLightmapEncoding.L1Half)
            foreach (var value in Data.Span) writer.Write(BitConverter.HalfToUInt16Bits(value));
        else writer.Write(QuantizedData.Span);
        writer.Flush();
        writer.Write(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        return stream.ToArray();
    }

    public static PbrLightmapAsset Decode(ReadOnlySpan<byte> bytes, int maximumBytes = 32 * 1024 * 1024)
    {
        if (maximumBytes < 0 || bytes.Length > maximumBytes || bytes.Length < 256
            || (!bytes[..8].SequenceEqual("SIALMAP1"u8) && !bytes[..8].SequenceEqual("SIALMAP2"u8) && !bytes[..8].SequenceEqual("SIALMAP3"u8)))
            throw new InvalidDataException("Invalid lightmap header or byte budget.");
        if (!SHA256.HashData(bytes[..^32]).AsSpan().SequenceEqual(bytes[^32..]))
            throw new InvalidDataException("Lightmap checksum mismatch.");
        using var stream = new MemoryStream(bytes[..^32].ToArray(), false);
        using var reader = new BinaryReader(stream);
        try {
            var quantized = bytes[..8].SequenceEqual("SIALMAP2"u8);
            stream.Position = 8;
            var chartCount = 0;
            var charted = bytes[..8].SequenceEqual("SIALMAP3"u8);
            if (charted) {
                var encoding = reader.ReadInt32();
                chartCount = reader.ReadInt32();
                if (encoding is < 0 or > 1 || chartCount is < 1 or > 1_000_000)
                    throw new InvalidDataException("Invalid lightmap chart header.");
                quantized = encoding == 1;
            }
            var resolution = reader.ReadInt32();
            var count = reader.ReadInt32();
            if (resolution is < 8 or > 8192 || count is < 1 or > 1_000_000
                || (charted ? 224L : 216L) + (long)count * (quantized ? 80 : 16) + chartCount * 20L
                    + (long)resolution * resolution * (quantized ? 16 : TexelBytes) != stream.Length)
                throw new InvalidDataException("Invalid lightmap section lengths.");
            var scene = reader.ReadBytes(32);
            var surface = reader.ReadBytes(32);
            var bake = reader.ReadBytes(32);
            float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var sky = new ProceduralSky { Horizon = Vector(), Zenith = Vector(), Ground = Vector(),
                SunDirection = Vector(), SunRadiance = Vector(), SunExponent = reader.ReadSingle(), Intensity = reader.ReadSingle() };
            var settings = new PbrLightmapBakeSettings { Sky = sky, TowardLight = Vector(), LightRadiance = Vector(),
                Samples = reader.ReadInt32(), MaximumDistance = reader.ReadSingle(), RayBias = reader.ReadSingle() };
            var receivers = new PbrLightmapReceiver[count];
            var scales = quantized ? new float4[count * 4] : [];
            for (var i = 0; i < count; i++) {
                receivers[i] = new(-1, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), default);
                if (quantized) for (var band = 0; band < 4; band++) scales[i * 4 + band] = new(Vector(), reader.ReadSingle());
            }
            var charts = new PbrLightmapChart[chartCount];
            for (var i = 0; i < charts.Length; i++)
                charts[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            PbrLightmapAsset asset;
            if (quantized) {
                asset = FromUnorm8(resolution, receivers, scene, surface, settings,
                    reader.ReadBytes(resolution * resolution * 16), scales, charts);
            } else {
                var coefficients = new Half[resolution * resolution * CoefficientCount * 4];
                for (var i = 0; i < coefficients.Length; i++) coefficients[i] = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
                asset = FromHalf(resolution, receivers, scene, surface, settings, coefficients, charts);
            }
            if (!asset.BakeIdentity.Span.SequenceEqual(bake)) throw new InvalidDataException("Lightmap bake identity disagrees with its manifest.");
            return asset;
        }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid lightmap values.", e); }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated lightmap.", e); }
    }

    internal static void WriteSettings(BinaryWriter writer, PbrLightmapBakeSettings settings)
    {
        void Vector(float3 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        var sky = settings.Sky;
        Vector(sky.Horizon); Vector(sky.Zenith); Vector(sky.Ground); Vector(sky.SunDirection); Vector(sky.SunRadiance);
        writer.Write(sky.SunExponent); writer.Write(sky.Intensity);
        Vector(settings.TowardLight); Vector(settings.LightRadiance);
        writer.Write(settings.Samples); writer.Write(settings.MaximumDistance); writer.Write(settings.RayBias);
    }
}
