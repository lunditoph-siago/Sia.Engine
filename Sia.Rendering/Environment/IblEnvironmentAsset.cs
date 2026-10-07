using Sia.Asset;
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

    public void Write(Stream destination) => ChecksummedAsset.Write<IblEnvironmentAsset, Codec>(destination, this);

    public static IblEnvironmentAsset Read(Stream source) => ChecksummedAsset.Read<IblEnvironmentAsset, Codec>(source);

    private readonly struct Codec : IChecksummedAssetCodec<IblEnvironmentAsset>
    {
        public static int HeaderBytes => 0;
        public static int GetReadSize(ReadOnlySpan<byte> header) => k_PayloadBytes;
        public static int GetWriteSize(IblEnvironmentAsset value) => k_PayloadBytes;

        public static void WritePayload(BinaryWriter writer, IblEnvironmentAsset value)
        {
            writer.Write("SIAENV\0\0"u8);
            void Vector(float3 v)
            {
                writer.Write(v.x);
                writer.Write(v.y);
                writer.Write(v.z);
            }
            Vector(value.Sky.Horizon);
            Vector(value.Sky.Zenith);
            Vector(value.Sky.Ground);
            Vector(value.Sky.SunDirection);
            Vector(value.Sky.SunRadiance);
            writer.Write(value.Sky.SunExponent);
            writer.Write(value.Sky.Intensity);
            foreach (var c in value.Coefficients.Span) {
                writer.Write(c.x);
                writer.Write(c.y);
                writer.Write(c.z);
                writer.Write(c.w);
            }
            foreach (var v in value.Cube.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
            foreach (var v in value.BrdfLut.Span)
                writer.Write(BitConverter.HalfToUInt16Bits(v));
        }

        public static IblEnvironmentAsset ReadPayload(BinaryReader reader)
        {
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
}
