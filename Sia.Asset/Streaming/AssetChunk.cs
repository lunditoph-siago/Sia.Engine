using System.Collections.Immutable;
using System.Security.Cryptography;
using K4os.Compression.LZ4;

namespace Sia.Asset;

public enum AssetChunkEncoding : byte { Raw, Lz4 }

public sealed class AssetChunk
{
    public const int MaximumLength = 16 * 1024 * 1024;

    public string Id { get; }
    public int Length { get; }
    public int DecodedLength { get; }
    public AssetChunkEncoding Encoding { get; }
    public ImmutableArray<string> Dependencies { get; }
    public string FileName => Id + ".chunk";

    public AssetChunk(string id, int length, IEnumerable<string>? dependencies = null)
        : this(id, length, length, AssetChunkEncoding.Raw, dependencies) { }

    public AssetChunk(string id, int length, int decodedLength, AssetChunkEncoding encoding,
        IEnumerable<string>? dependencies = null)
    {
        ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumLength);
        var decoded = decodedLength;
        if (decoded is < 1 or > MaximumLength || encoding is not (AssetChunkEncoding.Raw or AssetChunkEncoding.Lz4)
            || (encoding == AssetChunkEncoding.Raw ? decoded != length : length >= decoded))
            throw new ArgumentException("Invalid independent chunk encoding or decoded length.");
        Id = id;
        Length = length;
        DecodedLength = decoded;
        Encoding = encoding;
        Dependencies = dependencies?.Take(AssetChunkManifest.MaximumChunks + 1).ToImmutableArray() ?? [];
        if (Dependencies.Length > AssetChunkManifest.MaximumChunks) {
            throw new ArgumentException("Too many chunk dependencies.", nameof(dependencies));
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in Dependencies) {
            ValidateId(dependency);
            if (dependency == id || !unique.Add(dependency)) {
                throw new ArgumentException("Chunk dependencies must be distinct and cannot include itself.", nameof(dependencies));
            }
        }
    }

    public static AssetChunk FromBytes(ReadOnlySpan<byte> bytes, IEnumerable<string>? dependencies = null)
        => new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, dependencies);

    /// <summary>Offline lossless encoding; incompressible blocks retain their raw representation.</summary>
    public static (AssetChunk Chunk, byte[] Bytes) Encode(ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes.Length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, MaximumLength);
        var encoded = GC.AllocateUninitializedArray<byte>(bytes.Length);
        var written = LZ4Codec.Encode(bytes, encoded);
        if (written > 0 && written < bytes.Length) {
            Array.Resize(ref encoded, written);
            return (new(Convert.ToHexString(SHA256.HashData(encoded)), written,
                decodedLength: bytes.Length, encoding: AssetChunkEncoding.Lz4), encoded);
        }
        bytes.CopyTo(encoded);
        return (FromBytes(encoded), encoded);
    }

    /// <summary>Decodes authenticated wire bytes into an already admitted destination.</summary>
    public void Decode(ReadOnlySpan<byte> bytes, Span<byte> destination)
    {
        if (bytes.Length != Length || destination.Length != DecodedLength)
            throw new InvalidDataException("Chunk storage or decoded length mismatch.");
        if (Encoding == AssetChunkEncoding.Raw) { bytes.CopyTo(destination); return; }
        if (LZ4Codec.Decode(bytes, destination) != destination.Length)
            throw new InvalidDataException("Invalid, truncated or oversized compressed chunk.");
    }

    internal static void ValidateId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 64 || id.Any(static c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))) {
            throw new ArgumentException("A chunk ID must be an uppercase SHA-256 hexadecimal digest.", nameof(id));
        }
    }
}
