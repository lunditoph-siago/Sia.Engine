using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Sia.Asset;

/// <summary>One original wire file; its typed codec and content identity are unchanged by storage packing.</summary>
public sealed record AssetPackEntry(string FileName, AssetChunk Chunk, AssetChunkSlice Slice);

/// <summary>Immutable storage locations authenticated by a caller-supplied full index SHA-256.</summary>
public sealed class AssetPackIndex
{
    public const int MaximumEntries = 65536;
    public const int MaximumEncodedBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, AssetPackEntry> _byHash = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AssetPackEntry> Entries { get; }
    public int EncodedBytes { get; }

    public AssetPackIndex(IEnumerable<AssetPackEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var selected = entries.Take(MaximumEntries + 1).ToArray();
        if (selected.Length > MaximumEntries) throw new ArgumentException("Too many pack entries.", nameof(entries));
        var byName = new Dictionary<string, AssetPackEntry>(StringComparer.Ordinal);
        var portableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = new Dictionary<string, AssetChunk>(StringComparer.Ordinal);
        var regions = new Dictionary<string, List<(int Start, int End, string Hash)>>(StringComparer.Ordinal);
        long bytes = 48;
        foreach (var entry in selected) {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Chunk);
            var pack = entry.Slice.Pack;
            if (!ValidName(entry.FileName) || !portableNames.Add(entry.FileName))
                throw new ArgumentException("Duplicate or unsafe pack filename.", nameof(entries));
            if (entry.Chunk.Encoding != AssetChunkEncoding.Raw || entry.Chunk.Dependencies.Length != 0
                || pack is null || pack.Encoding != AssetChunkEncoding.Raw || pack.Dependencies.Length != 0
                || entry.Slice.Offset < 0 || (long)entry.Slice.Offset + entry.Chunk.Length > pack.Length)
                throw new ArgumentException("Invalid Raw wire descriptor or pack slice bounds.", nameof(entries));
            if (packs.TryGetValue(pack.Id, out var sharedPack)) {
                if (sharedPack.Length != pack.Length)
                    throw new ArgumentException("Conflicting pack lengths for one content hash.", nameof(entries));
            }
            else { sharedPack = pack; packs.Add(pack.Id, sharedPack); }
            AssetPackEntry normalized;
            if (_byHash.TryGetValue(entry.Chunk.Id, out var previous)) {
                if (previous.Chunk.Length != entry.Chunk.Length || previous.Slice.Pack.Id != pack.Id
                    || previous.Slice.Pack.Length != pack.Length || previous.Slice.Offset != entry.Slice.Offset)
                    throw new ArgumentException("Conflicting storage locations for one content hash.", nameof(entries));
                normalized = new(entry.FileName, previous.Chunk, new(sharedPack, entry.Slice.Offset));
            }
            else {
                normalized = new(entry.FileName, entry.Chunk, new(sharedPack, entry.Slice.Offset));
                _byHash.Add(entry.Chunk.Id, normalized);
                if (!regions.TryGetValue(pack.Id, out var ranges)) regions.Add(pack.Id, ranges = []);
                ranges.Add((entry.Slice.Offset, checked(entry.Slice.Offset + entry.Chunk.Length), entry.Chunk.Id));
            }
            // Canonicalization is scoped to this immutable index, never global string
            // interning. Thousands of leaves share the pack descriptor and its SHA.
            byName.Add(entry.FileName, normalized);
            bytes += 78L + entry.FileName.Length;
            if (bytes > MaximumEncodedBytes) throw new ArgumentException("Pack index exceeds its byte bound.", nameof(entries));
        }
        foreach (var ranges in regions.Values) {
            ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < ranges.Count; i++)
                if (ranges[i].Start < ranges[i - 1].End)
                    throw new ArgumentException("Different wire files overlap in a storage pack.", nameof(entries));
        }
        Entries = new ReadOnlyDictionary<string, AssetPackEntry>(byName);
        EncodedBytes = (int)bytes;
    }

    // The index describes stored wire bytes, not a texture chunk's decoded encoding.
    public AssetChunkSlice? Resolve(AssetChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_byHash.TryGetValue(chunk.Id, out var entry)) return null;
        if (entry.Chunk.Length != chunk.Length) throw new InvalidDataException("Pack leaf length differs from its requested content.");
        return entry.Slice;
    }

    public string Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[16]; "SIAPACK\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], EncodedBytes);
        stream.Write(header); hash.AppendData(header);
        Span<byte> name = stackalloc byte[255]; Span<byte> row = stackalloc byte[76]; Span<byte> size = stackalloc byte[2];
        foreach (var entry in Entries.Values.OrderBy(entry => entry.FileName, StringComparer.Ordinal)) {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)entry.FileName.Length);
            stream.Write(size); hash.AppendData(size);
            var count = Encoding.ASCII.GetBytes(entry.FileName, name);
            stream.Write(name[..count]); hash.AppendData(name[..count]);
            Convert.FromHexString(entry.Chunk.Id).CopyTo(row);
            BinaryPrimitives.WriteInt32LittleEndian(row[32..], entry.Chunk.Length);
            Convert.FromHexString(entry.Slice.Pack.Id).CopyTo(row[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(row[68..], entry.Slice.Pack.Length);
            BinaryPrimitives.WriteInt32LittleEndian(row[72..], entry.Slice.Offset);
            stream.Write(row); hash.AppendData(row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var footer = hash.GetCurrentHash(); stream.Write(footer); hash.AppendData(footer);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static AssetPackIndex Load(Stream stream, string expectedHash, int maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); AssetChunk.ValidateId(expectedHash);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 48) throw new InvalidDataException("Pack index exceeds its byte bound.");
        Span<byte> header = stackalloc byte[16]; stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var encoded = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (!header[..8].SequenceEqual("SIAPACK\0"u8) || count is < 0 or > MaximumEntries
            || encoded < 48L + count * 79L || encoded > maximumBytes || encoded > MaximumEncodedBytes
            || stream.CanSeek && stream.Length - stream.Position != encoded - 16L)
            throw new InvalidDataException("Invalid pack index header, count or available bytes.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        var entries = new List<AssetPackEntry>(count);
        Span<byte> size = stackalloc byte[2]; Span<byte> name = stackalloc byte[255]; Span<byte> row = stackalloc byte[76];
        var remaining = encoded - 48;
        try {
            for (var i = 0; i < count; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (remaining < 79) throw new InvalidDataException("Truncated pack index entries.");
                stream.ReadExactly(size); hash.AppendData(size);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(size);
                if (length is < 1 or > 255 || remaining < 78 + length)
                    throw new InvalidDataException("Invalid pack filename length.");
                stream.ReadExactly(name[..length]); hash.AppendData(name[..length]);
                if (name[..length].ContainsAnyExceptInRange((byte)1, (byte)127))
                    throw new InvalidDataException("Pack filenames must be portable ASCII basenames.");
                var filename = Encoding.ASCII.GetString(name[..length]);
                stream.ReadExactly(row); hash.AppendData(row);
                var leaf = new AssetChunk(Convert.ToHexString(row[..32]), BinaryPrimitives.ReadInt32LittleEndian(row[32..]));
                var pack = new AssetChunk(Convert.ToHexString(row[36..68]), BinaryPrimitives.ReadInt32LittleEndian(row[68..]));
                entries.Add(new(filename, leaf, new(pack, BinaryPrimitives.ReadInt32LittleEndian(row[72..]))));
                remaining -= 78 + length;
            }
            if (remaining != 0) throw new InvalidDataException("Trailing pack index entry bytes.");
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> footer = stackalloc byte[32]; stream.ReadExactly(footer);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetCurrentHash(), footer))
                throw new InvalidDataException("Pack index checksum mismatch.");
            hash.AppendData(footer);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)) || stream.ReadByte() != -1)
                throw new InvalidDataException("Pack index content hash or EOF mismatch.");
            return new(entries);
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid pack index entries.", error); }
        catch (OverflowException error) { throw new InvalidDataException("Invalid pack index bounds.", error); }
    }

    private static bool ValidName(string? name)
    {
        if (name is null || name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith('.')) return false;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && stem[3] is >= '1' and <= '9' && (stem.StartsWith("COM") || stem.StartsWith("LPT")));
    }
}
