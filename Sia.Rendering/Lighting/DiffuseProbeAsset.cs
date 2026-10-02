using System.Security.Cryptography;
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

    public void Write(Stream destination)
    {
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, true)) {
            w.Write("SIAPROBE"u8);
            w.Write(SceneIdentity.Span);
            w.Write(Origin.x);
            w.Write(Origin.y);
            w.Write(Origin.z);
            w.Write(Step.x);
            w.Write(Step.y);
            w.Write(Step.z);
            w.Write(Dimensions.x);
            w.Write(Dimensions.y);
            w.Write(Dimensions.z);
            foreach (var c in Coefficients.Span) {
                w.Write(c.x);
                w.Write(c.y);
                w.Write(c.z);
                w.Write(c.w);
            }
        }
        var bytes = payload.GetBuffer().AsSpan(0, (int)payload.Length);
        destination.Write(bytes);
        destination.Write(SHA256.HashData(bytes));
    }

    public static DiffuseProbeAsset Read(Stream source)
    {
        var header = new byte[76];
        source.ReadExactly(header);
        using var r = new BinaryReader(new MemoryStream(header));
        if (!r.ReadBytes(8).AsSpan().SequenceEqual("SIAPROBE"u8))
            throw new InvalidDataException("Unsupported probe asset.");
        var identity = r.ReadBytes(32);
        var origin = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var step = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var dim = new uint3(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
        if (dim.x > MaximumProbes || dim.y > MaximumProbes || dim.z > MaximumProbes)
            throw new InvalidDataException("Probe dimension exceeds limits.");
        var count = (ulong)dim.x * dim.y * dim.z;
        if (dim.x < 2 || dim.y < 2 || dim.z < 2 || count > MaximumProbes)
            throw new InvalidDataException("Probe asset dimensions exceed limits.");
        var bytes = new byte[checked((int)count * 9 * 16)];
        source.ReadExactly(bytes);
        Span<byte> hash = stackalloc byte[32];
        source.ReadExactly(hash);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(header);
        sha.AppendData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(hash, sha.GetHashAndReset()) || source.ReadByte() != -1)
            throw new InvalidDataException("Probe checksum mismatch or trailing data.");
        using var values = new BinaryReader(new MemoryStream(bytes));
        var coefficients = new float4[(int)count * 9];
        for (var i = 0; i < coefficients.Length; i++)
            coefficients[i] = new(values.ReadSingle(), values.ReadSingle(), values.ReadSingle(), values.ReadSingle());
        return new(origin, step, dim, identity, coefficients);
    }
}
