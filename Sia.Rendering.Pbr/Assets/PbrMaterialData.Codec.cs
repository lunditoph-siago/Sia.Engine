using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrMaterialData
{
    public const int EncodedBytes = 177;

    /// <summary>Writes fixed-size material metadata without texture bytes to a borrowed stream.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> bytes = stackalloc byte[EncodedBytes]; bytes.Clear(); "SIAMATR1"u8.CopyTo(bytes);
        bytes[8] = (byte)((DoubleSided ? 1 : 0) | (AlphaBlend ? 2 : 0));
        var p = Parameters;
        ReadOnlySpan<float> values = [p.BaseColor.x, p.BaseColor.y, p.BaseColor.z, p.Metallic, p.Roughness,
            p.EmissiveColor.x, p.EmissiveColor.y, p.EmissiveColor.z, p.EmissiveStrength,
            NormalScale, OcclusionStrength, Opacity, Transmission, Thickness];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(9 + i * 4)..], values[i]);
        WriteId(bytes[65..], BaseColor); WriteId(bytes[81..], Normal); WriteId(bytes[97..], MetallicRoughness);
        WriteId(bytes[113..], Occlusion); WriteId(bytes[129..], Emissive);
        SHA256.HashData(bytes[..145], bytes[145..]); stream.Write(bytes);
    }

    public static PbrMaterialData Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try {
            Span<byte> bytes = stackalloc byte[EncodedBytes]; stream.ReadExactly(bytes);
            if (!bytes[..8].SequenceEqual("SIAMATR1"u8) || (bytes[8] & ~3) != 0)
                throw new InvalidDataException("Invalid material signature or flags.");
            Span<byte> digest = stackalloc byte[32]; SHA256.HashData(bytes[..145], digest);
            if (!CryptographicOperations.FixedTimeEquals(digest, bytes[145..])) throw new InvalidDataException("Material checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing material metadata.");
            Span<float> f = stackalloc float[14];
            for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(9 + i * 4)..]);
            return new(new(new(f[0], f[1], f[2]), f[3], f[4], new(f[5], f[6], f[7]), f[8]),
                ReadId(bytes[65..]), ReadId(bytes[81..]), ReadId(bytes[97..]), ReadId(bytes[113..]), ReadId(bytes[129..]),
                f[9], f[10], (bytes[8] & 1) != 0, (bytes[8] & 2) != 0, f[11], f[12], f[13]);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated material metadata.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid material metadata.", error); }
    }

    private static void WriteId(Span<byte> bytes, TextureReference? reference)
    {
        if (reference is not null) reference.Value.Value.TryWriteBytes(bytes, bigEndian: true, out _);
    }

    private static TextureReference? ReadId(ReadOnlySpan<byte> bytes)
    {
        var id = new Guid(bytes[..16], bigEndian: true);
        return id == Guid.Empty ? null : new(new AssetId(id));
    }
}
