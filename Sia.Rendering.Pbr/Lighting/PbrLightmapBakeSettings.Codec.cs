using System.Buffers.Binary;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial record PbrLightmapBakeSettings
{
    // Fixed integrator input, not an extensible option bag or a versioned codec.
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
    }

    internal static PbrLightmapBakeSettings Decode(ReadOnlySpan<byte> bytes)
    {
        var settings = new PbrLightmapBakeSettings {
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

    private static void WriteVector(Span<byte> bytes, float3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.z);
    }
    private static float3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
}
