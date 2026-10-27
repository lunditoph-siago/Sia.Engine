using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class IblEnvironmentAsset
{
    public const int CubeSize = 128;
    public const int MipCount = 8;
    public const int LutSize = 128;
    public const int CubeTexels = 131070;
    public const int LutTexels = LutSize * LutSize;
    private const int k_PayloadBytes = 8 + (17 * 4) + (9 * 16) + ((CubeTexels + LutTexels) * 8);
    public const int EncodedBytes = k_PayloadBytes + 32;

    public ProceduralSky Sky { get; }
    public ReadOnlyMemory<float4> Coefficients { get; }
    public ReadOnlyMemory<Half> Cube { get; }
    public ReadOnlyMemory<Half> BrdfLut { get; }
    /// <summary>Physical radiance per stored cube RGB unit. SH RGB remains in physical units; the BRDF LUT is dimensionless.</summary>
    public float CubeRadianceScale { get; }

    /// <summary>Snapshots one validated independent payload; no IO, asset acquisition or GPU effects.</summary>
    public static IblEnvironmentAsset Create(ProceduralSky sky, ReadOnlySpan<float4> coefficients,
        ReadOnlySpan<Half> cube, ReadOnlySpan<Half> lut, ulong maximumBytes = 4ul * 1024 * 1024,
        float cubeRadianceScale = 1)
    {
        ArgumentNullException.ThrowIfNull(sky);
        if (coefficients.Length != 9 || cube.Length != CubeTexels * 4 || lut.Length != LutTexels * 4)
            throw new ArgumentException("Environment arrays must match the fixed payload shape.");
        if (9ul * 16 + ((ulong)cube.Length + (ulong)lut.Length) * 2 > maximumBytes)
            throw new ArgumentException("Environment payload exceeds its owned-array admission.", nameof(maximumBytes));
        ValidateScale(cubeRadianceScale, coefficients[0].w);
        return new(sky, coefficients.ToArray(), cube.ToArray(), lut.ToArray(), cubeRadianceScale);
    }

    /// <summary>Snapshots new filtered specular data, sharing this immutable sky, diffuse SH and BRDF payload.</summary>
    public IblEnvironmentAsset WithCube(ReadOnlySpan<Half> cube, ulong maximumBytes = 4ul * 1024 * 1024,
        CancellationToken cancellationToken = default, float? cubeRadianceScale = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cube.Length != CubeTexels * 4 || (ulong)cube.Length * 2 > maximumBytes)
            throw new ArgumentException("Filtered cube shape or owned-array budget is invalid.", nameof(cube));
        for (var at = 0; at < cube.Length; at++) {
            if ((at & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!Half.IsFinite(cube[at])) throw new InvalidDataException("Filtered cube must contain finite values.");
        }
        var scale = cubeRadianceScale ?? CubeRadianceScale;
        ValidateScale(scale, Coefficients.Span[0].w);
        var copied = cube.ToArray(); cancellationToken.ThrowIfCancellationRequested();
        return new(this, copied, scale);
    }

    private IblEnvironmentAsset(IblEnvironmentAsset source, Half[] cube, float scale)
    { Sky = source.Sky; Coefficients = source.Coefficients; BrdfLut = source.BrdfLut; Cube = cube; CubeRadianceScale = scale; }

    internal IblEnvironmentAsset(ProceduralSky sky, float4[] coefficients, Half[] cube, Half[] lut, float cubeRadianceScale = 1)
    {
        try { sky.ValidateData(); }
        catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("Invalid environment sky data.", error); }
        if (coefficients.Length != 9 || cube.Length != CubeTexels * 4 || lut.Length != LutTexels * 4
            || coefficients.Any(c => !math.all(math.isfinite(c)))
            || cube.Any(v => !Half.IsFinite(v)) || lut.Any(v => !Half.IsFinite(v)))
            throw new InvalidDataException("Invalid environment shape or non-finite data.");
        ValidateScale(cubeRadianceScale, coefficients[0].w);
        Sky = sky;
        Coefficients = coefficients;
        Cube = cube;
        BrdfLut = lut;
        CubeRadianceScale = cubeRadianceScale;
    }

    private static void ValidateScale(float scale, float reserved)
    {
        // Power-of-two scaling changes range without another rounding step before Half conversion.
        var bits = BitConverter.SingleToUInt32Bits(scale);
        if (scale < 1 || scale > MathF.ScaleB(1, 100) || !float.IsFinite(scale) || (bits & 0x7fffffu) != 0
            || reserved != 0)
            throw new InvalidDataException("Environment cube scale must be a power of two in [1, 2^100]; SH[0].w is reserved.");
    }

    /// <summary>Writes the fixed payload and returns its complete encoded SHA-256 content identity.</summary>
    public string Write(Stream destination)
    {
        using var payload = new MemoryStream(k_PayloadBytes);
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write("SIAIBL\0\0"u8);
            void Vector(float3 v)
            {
                writer.Write(v.x);
                writer.Write(v.y);
                writer.Write(v.z);
            }
            Vector(Sky.Horizon);
            Vector(Sky.Zenith);
            Vector(Sky.Ground);
            Vector(Sky.SunDirection);
            Vector(Sky.SunRadiance);
            writer.Write(Sky.SunExponent);
            writer.Write(Sky.Intensity);
            var coefficient = 0;
            foreach (var c in Coefficients.Span) {
                writer.Write(c.x);
                writer.Write(c.y);
                writer.Write(c.z);
                writer.Write(coefficient++ == 0 ? CubeRadianceScale : c.w);
            }
            foreach (var v in Cube.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
            foreach (var v in BrdfLut.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
        }
        var bytes = payload.GetBuffer().AsSpan(0, (int)payload.Length);
        var checksum = SHA256.HashData(bytes);
        destination.Write(bytes);
        destination.Write(checksum);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes); hash.AppendData(checksum);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static IblEnvironmentAsset Read(Stream source)
    {
        Span<byte> signature = stackalloc byte[8];
        source.ReadExactly(signature);
        if (!signature.SequenceEqual("SIAIBL\0\0"u8))
            throw new InvalidDataException("Unsupported environment asset.");
        var bytes = new byte[k_PayloadBytes];
        signature.CopyTo(bytes);
        source.ReadExactly(bytes.AsSpan(8));
        Span<byte> checksum = stackalloc byte[32];
        source.ReadExactly(checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(bytes)) || source.ReadByte() != -1)
            throw new InvalidDataException("Environment checksum mismatch or trailing data.");
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        reader.BaseStream.Position = 8;
        float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var sky = new ProceduralSky { Horizon = Vector(), Zenith = Vector(), Ground = Vector(), SunDirection = Vector(), SunRadiance = Vector(), SunExponent = reader.ReadSingle(), Intensity = reader.ReadSingle() };
        var sh = new float4[9];
        for (var i = 0; i < sh.Length; i++)
            sh[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var scale = sh[0].w;
        sh[0].w = 0;
        ValidateScale(scale, sh[0].w);
        Half[] Values(int count)
        {
            var result = new Half[count];
            for (var i = 0; i < count; i++)
                result[i] = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
            return result;
        }
        return new(sky, sh, Values(CubeTexels * 4), Values(LutTexels * 4), scale);
    }
}
