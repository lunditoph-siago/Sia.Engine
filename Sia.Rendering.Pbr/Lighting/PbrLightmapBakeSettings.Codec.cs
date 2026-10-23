using System.Buffers.Binary;
using Sia.Math;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial record PbrLightmapBakeSettings
{
    // Fixed procedural profile; HDR metadata appends one authenticated typed dependency.
    internal void Encode(Span<byte> bytes)
    {
        WriteVector(bytes, Sky.Horizon); WriteVector(bytes[12..], Sky.Zenith);
        WriteVector(bytes[24..], Sky.Ground); WriteVector(bytes[36..], Sky.SunRadiance);
        WriteVector(bytes[48..], Sky.SunDirection);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[60..], Sky.SunExponent);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[64..], Sky.Intensity);
        WriteVector(bytes[68..], TowardLight); WriteVector(bytes[80..], LightRadiance);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[92..], Samples);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[96..], MaximumDistance);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[100..], RayBias);
        if (Environment is { } environment) {
            environment.Id.Value.TryWriteBytes(bytes.Slice(104, 16), bigEndian: true, out _);
            _ = Convert.FromHexString(environment.Chunks[0].Id, bytes.Slice(120, 32), out _, out _);
        }
    }

    internal static PbrLightmapBakeSettings Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (104 or 152)) throw new InvalidDataException("Invalid fixed lightmap input profile length.");
        var settings = new PbrLightmapBakeSettings {
            Environment = bytes.Length == 104 ? null : new(new(new Guid(bytes.Slice(104, 16), bigEndian: true)),
                [new AssetChunk(Convert.ToHexString(bytes.Slice(120, 32)), IblEnvironmentAsset.EncodedBytes)]),
            Sky = new() {
                Horizon = ReadVector(bytes), Zenith = ReadVector(bytes[12..]), Ground = ReadVector(bytes[24..]),
                SunRadiance = ReadVector(bytes[36..]), SunDirection = ReadVector(bytes[48..]),
                SunExponent = BinaryPrimitives.ReadSingleLittleEndian(bytes[60..]),
                Intensity = BinaryPrimitives.ReadSingleLittleEndian(bytes[64..])
            },
            TowardLight = ReadVector(bytes[68..]), LightRadiance = ReadVector(bytes[80..]),
            Samples = BinaryPrimitives.ReadInt32LittleEndian(bytes[92..]),
            MaximumDistance = BinaryPrimitives.ReadSingleLittleEndian(bytes[96..]),
            RayBias = BinaryPrimitives.ReadSingleLittleEndian(bytes[100..])
        };
        settings.Validate(); return settings;
    }

    internal bool SameInputs(PbrLightmapBakeSettings other)
    {
        if (InputBytes != other.InputBytes) return false;
        Span<byte> first = stackalloc byte[152]; Span<byte> second = stackalloc byte[152];
        Encode(first); other.Encode(second); return first[..InputBytes].SequenceEqual(second[..InputBytes]);
    }

    /// <summary>Validates borrowed decoded HDR content before producer or renderer acquisition/effects.</summary>
    public void ValidateEnvironment(IblEnvironmentAsset? environment)
    {
        Validate();
        if (Environment is null) {
            if (environment is not null) throw new ArgumentException("HDR lightmap input must be declared in its bake identity.", nameof(environment));
        }
        else if (environment is null || environment.Write(Stream.Null) != Environment.Chunks[0].Id)
            throw new InvalidDataException("HDR lightmap environment differs from its frozen typed content.");
    }

    private static void WriteVector(Span<byte> bytes, float3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.z);
    }
    private static float3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
}
