using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Sia.Asset;

public sealed class AssetChunkManifest
{
    public const int MaximumChunks = 65536;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, AssetChunk> _byId;
    public ImmutableArray<AssetChunk> Chunks { get; }
    public ImmutableArray<string> Bootstrap { get; }

    public AssetChunkManifest(IEnumerable<AssetChunk> chunks, IEnumerable<string> bootstrap)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(bootstrap);
        Chunks = chunks.Take(MaximumChunks + 1).ToImmutableArray();
        if (Chunks.Length > MaximumChunks) { throw new ArgumentException("Too many chunks.", nameof(chunks)); }
        _byId = new(StringComparer.Ordinal);
        long size = 16;
        foreach (var chunk in Chunks) {
            ArgumentNullException.ThrowIfNull(chunk);
            if (!_byId.TryAdd(chunk.Id, chunk)) { throw new ArgumentException("Duplicate chunk ID.", nameof(chunks)); }
            size += 40L + chunk.Dependencies.Length * 4L;
        }
        Bootstrap = bootstrap.Take(MaximumChunks + 1).ToImmutableArray();
        if (Bootstrap.Length > MaximumChunks || Bootstrap.Distinct(StringComparer.Ordinal).Count() != Bootstrap.Length) {
            throw new ArgumentException("Invalid bootstrap chunk list.", nameof(bootstrap));
        }
        size += Bootstrap.Length * 4L;
        if (size > MaximumBytes) { throw new ArgumentException("Manifest exceeds the byte limit.", nameof(chunks)); }
        foreach (var id in Bootstrap) { _ = GetChunk(id); }
        // Kahn's algorithm checks the whole graph without recursively trusting its depth.
        var incoming = Chunks.ToDictionary(c => c.Id, c => c.Dependencies.Length, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chunk in Chunks) {
            foreach (var dependency in chunk.Dependencies) {
                _ = GetChunk(dependency);
                if (!dependents.TryGetValue(dependency, out var users)) { dependents[dependency] = users = []; }
                users.Add(chunk.Id);
            }
        }
        var ready = new Queue<string>(Chunks.Where(c => incoming[c.Id] == 0).Select(c => c.Id));
        var visited = 0;
        while (ready.TryDequeue(out var id)) {
            visited++;
            if (!dependents.TryGetValue(id, out var users)) { continue; }
            foreach (var user in users) { if (--incoming[user] == 0) { ready.Enqueue(user); } }
        }
        if (visited != Chunks.Length) { throw new ArgumentException("Chunk dependency cycle.", nameof(chunks)); }
    }

    public AssetChunk GetChunk(string id) => _byId.TryGetValue(id, out var chunk)
        ? chunk : throw new ArgumentException("The manifest references a missing chunk.", nameof(id));

    public ImmutableArray<AssetChunk> GetRequiredChunks(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<AssetChunk>();
        var pending = new Stack<(AssetChunk Chunk, bool Expanded)>();
        foreach (var root in roots) {
            pending.Push((GetChunk(root), false));
            while (pending.TryPop(out var item)) {
                if (item.Expanded) { result.Add(item.Chunk); continue; }
                if (!seen.Add(item.Chunk.Id)) { continue; }
                pending.Push((item.Chunk, true));
                for (var i = item.Chunk.Dependencies.Length - 1; i >= 0; i--) {
                    pending.Push((GetChunk(item.Chunk.Dependencies[i]), false));
                }
            }
        }
        return result.ToImmutable();
    }

    public byte[] Encode()
    {
        var indices = Chunks.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("SIACHNK1"u8);
        writer.Write(Chunks.Length);
        writer.Write(Bootstrap.Length);
        foreach (var chunk in Chunks) {
            writer.Write(Convert.FromHexString(chunk.Id));
            writer.Write(chunk.Length);
            writer.Write(chunk.Dependencies.Length);
            foreach (var dependency in chunk.Dependencies) { writer.Write(indices[dependency]); }
        }
        foreach (var id in Bootstrap) { writer.Write(indices[id]); }
        return output.ToArray();
    }

    public static AssetChunkManifest Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 16 or > MaximumBytes || !bytes[..8].SequenceEqual("SIACHNK1"u8)) {
            throw new InvalidDataException("Invalid chunk manifest header or size.");
        }
        var reader = new ManifestReader(bytes[8..]);
        var count = reader.Count(MaximumChunks);
        var bootstrapCount = reader.Count(MaximumChunks);
        if ((long)count * 40 + (long)bootstrapCount * 4 > reader.Remaining) {
            throw new InvalidDataException("Truncated chunk manifest.");
        }
        var ids = new string[count];
        var lengths = new int[count];
        var dependencies = new int[count][];
        for (var i = 0; i < count; i++) {
            ids[i] = Convert.ToHexString(reader.Bytes(32));
            lengths[i] = reader.Count(AssetChunk.MaximumLength);
            var n = reader.Count(count);
            if ((long)n * 4 > reader.Remaining) { throw new InvalidDataException("Truncated dependencies."); }
            dependencies[i] = new int[n];
            for (var j = 0; j < n; j++) { dependencies[i][j] = reader.Index(count); }
        }
        var bootstrap = new string[bootstrapCount];
        for (var i = 0; i < bootstrap.Length; i++) { bootstrap[i] = ids[reader.Index(count)]; }
        if (reader.Remaining != 0) { throw new InvalidDataException("Trailing manifest bytes."); }
        try {
            return new(ids.Select((id, i) => new AssetChunk(id, lengths[i], dependencies[i].Select(index => ids[index]))), bootstrap);
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid chunk manifest graph.", error); }
    }

    private ref struct ManifestReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        public int Remaining => _bytes.Length;
        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count > _bytes.Length) { throw new InvalidDataException("Truncated manifest."); }
            var result = _bytes[..count];
            _bytes = _bytes[count..];
            return result;
        }
        public int Count(int maximum)
        {
            var result = BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
            if (result < 0 || result > maximum) { throw new InvalidDataException("Manifest count exceeds its limit."); }
            return result;
        }
        public int Index(int count)
        {
            var index = Count(int.MaxValue);
            if (index >= count) { throw new InvalidDataException("Invalid chunk index."); }
            return index;
        }
    }
}
