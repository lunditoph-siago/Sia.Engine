#if !BROWSER
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Offline layout policy. Logical mip IDs are authored; chunk hashes identify immutable content only.
internal static partial class TextureCook
{
    internal sealed record Recipe
    {
        public required Guid Texture { get; init; }
        public required string Input { get; init; }
        public required Guid[] Mips { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4)]
    [JsonSerializable(typeof(Recipe))]
    private partial class TextureRecipeJsonContext : JsonSerializerContext;

    internal static AssetId Cook(string recipePath, string outputDirectory,
        ulong maximumBytes = 128ul * 1024 * 1024, ulong maximumTotalBytes = 512ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        recipePath = Path.GetFullPath(recipePath);
        var recipe = AssetInput.ReadJson(recipePath, TextureRecipeJsonContext.Default.Recipe, 4096);
        if (recipe.Texture == Guid.Empty || string.IsNullOrWhiteSpace(recipe.Input) || recipe.Mips is null
            || recipe.Mips.Length > 13 || recipe.Mips.Any(id => id == Guid.Empty || id == recipe.Texture)
            || recipe.Mips.Distinct().Count() != recipe.Mips.Length)
            throw new InvalidDataException("Texture cook requires distinct authored texture/mip IDs.");
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Texture output requires an existing parent.");
        if (Path.Exists(output)) throw new IOException("Texture output must not already exist.");
        var input = Path.GetFullPath(recipe.Input, Path.GetDirectoryName(recipePath)!);
        var record = FileLoader.Load<PbrTextureRecord, ulong>(input, maximumBytes);
        if (record.Mips is not null || recipe.Mips.Length >= record.Data.MipLevels.Length)
            throw new InvalidDataException("Texture cook requires a whole texture and at least one bootstrap level.");
        var source = record.Data;
        var stage = Path.Combine(parent, ".texture-cook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var files = new List<string>(); var published = new HashSet<string>(StringComparer.Ordinal);
        ulong total = 0;
        void RequireStage() {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Texture staging directory must not be a link.");
        }
        void Admit(ulong bytes) {
            if (bytes > maximumBytes || bytes > maximumTotalBytes - total)
                throw new InvalidDataException("Texture products exceed their publication byte limit.");
            total += bytes;
        }
        try {
            var mips = new PbrTextureMipReference[recipe.Mips.Length];
            // One fixed chunk buffer is shared by all mip encoders; pixels remain borrowed immutable slices.
            var buffer = mips.Length == 0 ? [] : new byte[AssetChunk.MaximumLength];
            for (var mip = 0; mip < mips.Length; mip++) {
                cancellationToken.ThrowIfCancellationRequested();
                using var encoded = new ChunkOutput(buffer, (chunk, bytes) => {
                    cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                    if (!published.Add(chunk.Id)) return;
                    Admit((ulong)chunk.Length);
                    var path = Path.Combine(stage, chunk.FileName);
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    files.Add(path); file.Write(bytes.Span);
                }, cancellationToken);
                source.SliceMipChain(mip, 1).Save(encoded);
                var (chunks, digest) = encoded.Finish();
                mips[mip] = new(new(new(recipe.Mips[mip]), chunks), digest);
            }
            var index = PbrTextureMipsData.Create(source.Width, source.Height, source.SliceMipChain(mips.Length), mips);
            Admit(index.EncodedBytes); cancellationToken.ThrowIfCancellationRequested(); RequireStage();
            var rootPath = Path.Combine(stage, recipe.Texture.ToString("N") + ".siaasset");
            using (var file = new FileStream(rootPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                files.Add(rootPath); index.Save(file);
            }
            cancellationToken.ThrowIfCancellationRequested();
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return new(recipe.Texture);
        }
        catch (Exception error) {
            try {
                OfflineDirectory.RequireFiles(stage, files, CancellationToken.None);
                foreach (var file in files) File.Delete(file);
                Directory.Delete(stage, recursive: false);
            }
            catch (Exception cleanup) { throw new AggregateException("Texture cook failed and staging cleanup was incomplete.", error, cleanup); }
            throw;
        }
    }

    private sealed class ChunkOutput(byte[] buffer, Action<AssetChunk, ReadOnlyMemory<byte>> write,
        CancellationToken token) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly List<AssetChunk> _chunks = [];
        private int _used;
        private bool _finished;
        public (AssetChunk[] Chunks, string Hash) Finish() {
            ObjectDisposedException.ThrowIf(_finished, this);
            Flush(); _finished = true;
            return (_chunks.ToArray(), Convert.ToHexString(_hash.GetHashAndReset()));
        }
        public override void Write(ReadOnlySpan<byte> bytes) {
            ObjectDisposedException.ThrowIf(_finished, this);
            while (!bytes.IsEmpty) {
                token.ThrowIfCancellationRequested();
                var count = System.Math.Min(bytes.Length, buffer.Length - _used);
                bytes[..count].CopyTo(buffer.AsSpan(_used)); _hash.AppendData(bytes[..count]);
                _used += count; bytes = bytes[count..];
                if (_used == buffer.Length) Flush();
            }
        }
        public override void Flush() {
            token.ThrowIfCancellationRequested();
            if (_used == 0) return;
            var chunk = AssetChunk.FromBytes(buffer.AsSpan(0, _used));
            write(chunk, buffer.AsMemory(0, _used)); _chunks.Add(chunk); _used = 0;
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
        public override bool CanWrite => !_finished;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
    }
}
#endif
