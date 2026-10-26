using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrMaterialData
{
    public const int EncodedBytes = 177;
    public const int MaximumEncodedBytes = 265;

    public int EncodedSize => DiffuseMoments is null ? EncodedBytes : MaximumEncodedBytes;

    /// <summary>Writes versioned material metadata without texture bytes to a borrowed stream.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> storage = stackalloc byte[MaximumEncodedBytes];
        var bytes = storage[..EncodedSize]; bytes.Clear();
        if (DiffuseMoments is null) "SIAMATR1"u8.CopyTo(bytes);
        else "SIAMATR2"u8.CopyTo(bytes);
        bytes[8] = (byte)((DoubleSided ? 1 : 0) | (AlphaBlend ? 2 : 0));
        var p = Parameters;
        ReadOnlySpan<float> values = [p.BaseColor.x, p.BaseColor.y, p.BaseColor.z, p.Metallic, p.Roughness,
            p.EmissiveColor.x, p.EmissiveColor.y, p.EmissiveColor.z, p.EmissiveStrength,
            NormalScale, OcclusionStrength, Opacity, Transmission, Thickness];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(9 + i * 4)..], values[i]);
        WriteId(bytes[65..], BaseColor); WriteId(bytes[81..], Normal); WriteId(bytes[97..], MetallicRoughness);
        WriteId(bytes[113..], Occlusion); WriteId(bytes[129..], Emissive);
        var bodyBytes = 145;
        if (DiffuseMoments is { } moments) {
            ReadOnlySpan<float> means = [moments.BaseColorMean.x, moments.BaseColorMean.y, moments.BaseColorMean.z,
                moments.BaseColorMetallicMean.x, moments.BaseColorMetallicMean.y, moments.BaseColorMetallicMean.z];
            for (var i = 0; i < means.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(145 + i * 4)..], means[i]);
            WriteSourceHash(bytes.Slice(169, 32), moments.BaseColorHash);
            WriteSourceHash(bytes.Slice(201, 32), moments.MetallicRoughnessHash);
            bodyBytes = 233;
        }
        SHA256.HashData(bytes[..bodyBytes], bytes[bodyBytes..]); stream.Write(bytes);
    }

    public static PbrMaterialData Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try {
            Span<byte> storage = stackalloc byte[MaximumEncodedBytes]; stream.ReadExactly(storage[..8]);
            var version2 = storage[..8].SequenceEqual("SIAMATR2"u8);
            if (!version2 && !storage[..8].SequenceEqual("SIAMATR1"u8))
                throw new InvalidDataException("Invalid material signature.");
            var bytes = storage[..(version2 ? MaximumEncodedBytes : EncodedBytes)]; stream.ReadExactly(bytes[8..]);
            if ((bytes[8] & ~3) != 0)
                throw new InvalidDataException("Invalid material signature or flags.");
            var bodyBytes = version2 ? 233 : 145;
            Span<byte> digest = stackalloc byte[32]; SHA256.HashData(bytes[..bodyBytes], digest);
            if (!CryptographicOperations.FixedTimeEquals(digest, bytes[bodyBytes..])) throw new InvalidDataException("Material checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing material metadata.");
            Span<float> f = stackalloc float[14];
            for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(9 + i * 4)..]);
            var baseColor = ReadId(bytes[65..]); var normal = ReadId(bytes[81..]);
            var metallicRoughness = ReadId(bytes[97..]); var occlusion = ReadId(bytes[113..]); var emissive = ReadId(bytes[129..]);
            PbrDiffuseMoments? moments = null;
            if (version2) {
                Span<float> means = stackalloc float[6];
                for (var i = 0; i < means.Length; i++) means[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(145 + i * 4)..]);
                moments = new(new(means[0], means[1], means[2]), new(means[3], means[4], means[5]),
                    ReadSourceHash(bytes.Slice(169, 32), baseColor is not null),
                    ReadSourceHash(bytes.Slice(201, 32), metallicRoughness is not null));
            }
            return new(new(new(f[0], f[1], f[2]), f[3], f[4], new(f[5], f[6], f[7]), f[8]),
                baseColor, normal, metallicRoughness, occlusion, emissive,
                f[9], f[10], (bytes[8] & 1) != 0, (bytes[8] & 2) != 0, f[11], f[12], f[13], moments);
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

    private static void WriteSourceHash(Span<byte> bytes, string? hash)
    {
        if (hash is null) return;
        if (Convert.FromHexString(hash, bytes, out var consumed, out var written) != System.Buffers.OperationStatus.Done
            || consumed != 64 || written != 32)
            throw new ArgumentException("Invalid diffuse moment source hash.");
    }

    private static string? ReadSourceHash(ReadOnlySpan<byte> bytes, bool present)
    {
        if (present) return Convert.ToHexString(bytes);
        foreach (var value in bytes)
            if (value != 0) throw new InvalidDataException("An absent diffuse texture role cannot have a source hash.");
        return null;
    }
}
