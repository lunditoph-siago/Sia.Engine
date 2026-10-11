#if !BROWSER
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Example;

// Offline application composition; domain builders and asset infrastructure stay independent.
internal static partial class MeshCook
{
    internal const int MaximumRecipeBytes = 64 * 1024;

    internal sealed record Recipe
    {
        public required Guid Mesh { get; init; }
        public required Guid Source { get; init; }
        public required string Input { get; init; }
        public Guid? Raster { get; init; }
        public VirtualProduct? Virtual { get; init; }
        public BvhProduct? Bvh { get; init; }
    }

    internal sealed record VirtualProduct
    {
        public required Guid Id { get; init; }
        public MeshPatchBuildSettings? Settings { get; init; }
    }

    internal sealed record BvhProduct
    {
        public required Guid Id { get; init; }
        public MeshBvhBuildSettings? Settings { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 8)]
    [JsonSerializable(typeof(Recipe))]
    private partial class RecipeJsonContext : JsonSerializerContext;

    internal static AssetId Cook(string recipePath, string outputDirectory,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Mesh output requires an existing parent directory.");
        if (Path.Exists(output)) throw new IOException("Mesh output must not already exist.");
        recipePath = Path.GetFullPath(recipePath);
        var recipe = ReadRecipe(recipePath);
        Validate(recipe);
        cancellationToken.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(recipe.Input, Path.GetDirectoryName(recipePath)!);
        var record = FileLoader.Load<MeshSourceRecord, ulong>(input, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();

        using var world = new World();
        world.AddAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<MeshSourceAsset, MeshSourceRecord>();
        AssetLibrary.RegisterAsset<MeshManifestAsset, MeshManifestRecord>();
        var sourceEntity = world.AcquireAsset(new AssetId(recipe.Source), () => record);
        var source = sourceEntity.Get<MeshSourceAsset>().Source;
        var stage = Path.Combine(parent, ".mesh-cook-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(stage)) throw new IOException("Mesh staging path already exists.");
        Directory.CreateDirectory(stage);
        var files = new List<string>(5);
        void RequireStagingDirectory()
        {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Mesh staging directory must not be a directory link.");
        }
        try {
            void Write(Guid id, Action<Stream> encode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireStagingDirectory();
                var path = Path.Combine(stage, id.ToString("N") + ".siaasset");
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                files.Add(path);
                using var bounded = new OutputStream(file, maximumBytes, cancellationToken);
                encode(bounded);
            }

            MeshProductReference<TRecord> Product<TAsset, TRecord>(Guid id, Func<TRecord> build,
                Func<TAsset, string> key, Action<TAsset, Stream> encode)
                where TAsset : struct, IAsset<TAsset, TRecord>
                where TRecord : class, IAssetRecord
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = build();
                cancellationToken.ThrowIfCancellationRequested();
                AssetLibrary.RegisterAsset<TAsset, TRecord>();
                var asset = world.AcquireAsset(new AssetId(id), () => prepared);
                try {
                    var value = asset.Get<TAsset>();
                    Write(id, s => encode(value, s));
                    return new(new AssetId(id), key(value));
                }
                finally { asset.Destroy(); }
            }

            Write(recipe.Source, source.Save);
            var raster = recipe.Raster is { } rasterId
                ? Product<MeshRasterAsset, MeshRasterRecord>(rasterId,
                    () => new() { Data = MeshRasterData.Build(source) }, a => a.Data.BuildKey, (a, s) => a.Data.Save(s)) : null;
            var virtualData = recipe.Virtual is { } virtualRecipe
                ? Product<MeshVirtualAsset, MeshVirtualRecord>(virtualRecipe.Id,
                    () => new() { Data = MeshVirtualData.Build(source, virtualRecipe.Settings, maximumBytes, cancellationToken) },
                    a => a.Data.BuildKey, (a, s) => a.Data.Save(s)) : null;
            var bvh = recipe.Bvh is { } bvhRecipe
                ? Product<MeshBvhAsset, MeshBvhRecord>(bvhRecipe.Id,
                    () => new() { Data = MeshBvhData.Build(source, bvhRecipe.Settings, maximumBytes, cancellationToken) },
                    a => a.Data.BuildKey, (a, s) => a.Data.Save(s)) : null;
            var manifest = MeshManifest.Create(new(recipe.Source), source, raster, bvh, virtualData);
            var mesh = world.AcquireAsset(new AssetId(recipe.Mesh), () => new MeshManifestRecord { Data = manifest });
            Write(recipe.Mesh, mesh.Get<MeshManifestAsset>().Data.Save);
            cancellationToken.ThrowIfCancellationRequested();
            RequireStagingDirectory();
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return new(recipe.Mesh);
        }
        catch (Exception error) {
            // Exact files only; preserve unexpected files instead of deleting a directory recursively.
            try { RequireStagingDirectory(); }
            catch (Exception cleanup) { throw new AggregateException("Mesh cook failed; staging was preserved because its directory could not be verified.", error, cleanup); }
            var failures = new List<Exception>();
            foreach (var path in files) {
                try { File.Delete(path); } catch (Exception cleanup) { failures.Add(cleanup); }
            }
            try { Directory.Delete(stage, recursive: false); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count != 0) throw new AggregateException("Mesh cook failed and staging cleanup was incomplete.", [error, .. failures]);
            throw;
        }
    }

    private static Recipe ReadRecipe(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumRecipeBytes) throw new InvalidDataException("Mesh recipe exceeds 64 KiB.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Mesh recipe changed while reading.");
        return JsonSerializer.Deserialize(bytes, RecipeJsonContext.Default.Recipe)
            ?? throw new InvalidDataException("Mesh recipe must be an object.");
    }

    private static void Validate(Recipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Input);
        var ids = new HashSet<Guid>();
        void Claim(Guid id)
        {
            if (id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException("Mesh, Source and selected products require distinct nonempty IDs.");
        }
        Claim(recipe.Mesh); Claim(recipe.Source);
        if (recipe.Raster is { } raster) Claim(raster);
        if (recipe.Virtual is { } virtualData) Claim(virtualData.Id);
        if (recipe.Bvh is { } bvh) Claim(bvh.Id);
    }

    // Owns one output file. The limit is encoded bytes per file, not whole-process memory.
    private sealed class OutputStream(Stream file, ulong limit, CancellationToken cancellationToken) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((ulong)_written + (uint)buffer.Length > limit)
                throw new InvalidDataException("Encoded Mesh output exceeds its per-file byte limit.");
            file.Write(buffer); _written += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value) { ReadOnlySpan<byte> one = [value]; Write(one); }
        public override void Flush() => file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
    }
}
#endif
