using System.Buffers.Binary;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed class DiffuseProbeAsset
{
    public const int MaximumProbes = 4096;

    public float3 Origin { get; }
    public float3 Step { get; }
    public uint3 Dimensions { get; }
    public ReadOnlyMemory<float4> Coefficients { get; }
    public ReadOnlyMemory<byte> SceneIdentity { get; }

    public int Count => checked((int)(Dimensions.x * Dimensions.y * Dimensions.z));

    public DiffuseProbeAsset(
        float3 origin,
        float3 step,
        uint3 dimensions,
        ReadOnlySpan<byte> sceneIdentity,
        ReadOnlySpan<float4> coefficients)
    {
        if (math.any(dimensions > MaximumProbes)) throw new ArgumentException("Probe dimension exceeds limits.");

        var count = (ulong)dimensions.x * dimensions.y * dimensions.z;
        if (!math.all(math.isfinite(origin)) || !math.all(math.isfinite(step))
            || math.any(step <= 0) || math.any(dimensions < 2) || count > MaximumProbes
            || sceneIdentity.Length != 32 || coefficients.Length != (long)count * 9)
            throw new ArgumentException("Invalid probe volume shape or scene identity.");

        foreach (var c in coefficients)
            if (!math.all(math.isfinite(c)) || c.w < 0 || c.w > 1)
                throw new ArgumentException("Invalid probe coefficient or validity.");

        Origin = origin;
        Step = step;
        Dimensions = dimensions;
        SceneIdentity = sceneIdentity.ToArray();
        Coefficients = coefficients.ToArray();
    }

    internal float4[] Packed()
    {
        var data = new float4[3 + (Count * 9)];
        data[0] = new(Origin, Dimensions.x);
        data[1] = new(Step, Dimensions.y);
        data[2] = new(Dimensions.z, Count, 0, 0);
        Coefficients.Span.CopyTo(data.AsSpan(3));
        return data;
    }

    public void Write(Stream destination) => ChecksummedAsset.Write<DiffuseProbeAsset, Codec>(destination, this);

    public static DiffuseProbeAsset Read(Stream source) => ChecksummedAsset.Read<DiffuseProbeAsset, Codec>(source);

    private readonly struct Codec : IChecksummedAssetCodec<DiffuseProbeAsset>
    {
        public static int HeaderBytes => 76;
        public static int GetWriteSize(DiffuseProbeAsset value) => checked(HeaderBytes + value.Count * 9 * 16);

        public static int GetReadSize(ReadOnlySpan<byte> header)
        {
            if (!header[..8].SequenceEqual("SIAPROBE"u8))
                throw new InvalidDataException("Unsupported probe asset.");
            var x = BinaryPrimitives.ReadUInt32LittleEndian(header[64..]);
            var y = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
            var z = BinaryPrimitives.ReadUInt32LittleEndian(header[72..]);
            if (x > MaximumProbes || y > MaximumProbes || z > MaximumProbes)
                throw new InvalidDataException("Probe dimension exceeds limits.");
            var count = (ulong)x * y * z;
            if (x < 2 || y < 2 || z < 2 || count > MaximumProbes)
                throw new InvalidDataException("Probe asset dimensions exceed limits.");
            return checked(HeaderBytes + (int)count * 9 * 16);
        }

        public static void WritePayload(BinaryWriter w, DiffuseProbeAsset value)
        {
            w.Write("SIAPROBE"u8);
            w.Write(value.SceneIdentity.Span);
            w.Write(value.Origin.x);
            w.Write(value.Origin.y);
            w.Write(value.Origin.z);
            w.Write(value.Step.x);
            w.Write(value.Step.y);
            w.Write(value.Step.z);
            w.Write(value.Dimensions.x);
            w.Write(value.Dimensions.y);
            w.Write(value.Dimensions.z);
            foreach (var c in value.Coefficients.Span) {
                w.Write(c.x);
                w.Write(c.y);
                w.Write(c.z);
                w.Write(c.w);
            }
        }

        public static DiffuseProbeAsset ReadPayload(BinaryReader reader)
        {
            reader.ReadBytes(8); // Magic and bounded dimensions were validated in GetReadSize.
            var identity = reader.ReadBytes(32);
            var origin = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var step = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var dimensions = new uint3(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            var count = checked((int)(dimensions.x * dimensions.y * dimensions.z));
            var coefficients = new float4[count * 9];
            for (var i = 0; i < coefficients.Length; i++)
                coefficients[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return new(origin, step, dimensions, identity, coefficients);
        }
    }
}
