using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Sia.Asset;

public sealed class AssetChunk
{
    public const int MaximumLength = 16 * 1024 * 1024;

    public string Id { get; }
    public int Length { get; }
    public ImmutableArray<string> Dependencies { get; }
    public string FileName => Id + ".chunk";

    public AssetChunk(string id, int length, IEnumerable<string>? dependencies = null)
    {
        ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumLength);
        Id = id;
        Length = length;
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

    internal static void ValidateId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 64 || id.Any(static c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))) {
            throw new ArgumentException("A chunk ID must be an uppercase SHA-256 hexadecimal digest.", nameof(id));
        }
    }
}
