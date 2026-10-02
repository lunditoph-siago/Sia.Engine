using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed class IblEnvironmentAsset
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

    internal IblEnvironmentAsset(ProceduralSky sky, float4[] coefficients, Half[] cube, Half[] lut)
    {
        sky.Validate();
        if (coefficients.Length != 9 || cube.Length != CubeTexels * 4 || lut.Length != LutTexels * 4
            || coefficients.Any(c => !math.all(math.isfinite(c)))
            || cube.Any(v => !Half.IsFinite(v)) || lut.Any(v => !Half.IsFinite(v)))
            throw new InvalidDataException("Invalid environment shape or non-finite data.");
        Sky = sky;
        Coefficients = coefficients;
        Cube = cube;
        BrdfLut = lut;
    }

    public void Write(Stream destination)
    {
        using var payload = new MemoryStream(k_PayloadBytes);
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write("SIAENV\0\0"u8);
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
            foreach (var c in Coefficients.Span) {
                writer.Write(c.x);
                writer.Write(c.y);
                writer.Write(c.z);
                writer.Write(c.w);
            }
            foreach (var v in Cube.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
            foreach (var v in BrdfLut.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
        }
        var bytes = payload.GetBuffer().AsSpan(0, (int)payload.Length);
        destination.Write(bytes);
        destination.Write(SHA256.HashData(bytes));
    }

    public static IblEnvironmentAsset Read(Stream source)
    {
        var bytes = new byte[k_PayloadBytes];
        source.ReadExactly(bytes);
        Span<byte> checksum = stackalloc byte[32];
        source.ReadExactly(checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(bytes)) || source.ReadByte() != -1)
            throw new InvalidDataException("Environment checksum mismatch or trailing data.");
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("SIAENV\0\0"u8))
            throw new InvalidDataException("Unsupported environment asset.");
        float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var sky = new ProceduralSky { Horizon = Vector(), Zenith = Vector(), Ground = Vector(), SunDirection = Vector(), SunRadiance = Vector(), SunExponent = reader.ReadSingle(), Intensity = reader.ReadSingle() };
        var sh = new float4[9];
        for (var i = 0; i < sh.Length; i++)
            sh[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Half[] Values(int count)
        {
            var result = new Half[count];
            for (var i = 0; i < count; i++)
                result[i] = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
            return result;
        }
        return new(sky, sh, Values(CubeTexels * 4), Values(LutTexels * 4));
    }
}
