#if !BROWSER
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sia.Engine.Example;

internal static partial class ExampleContent
{
    internal sealed record Catalog
    {
        public required string Bunny { get; init; }
        public string? BistroExterior { get; init; }
        public string? BistroInterior { get; init; }
        public string? BistroWine { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 4)]
    [JsonSerializable(typeof(Catalog))]
    private partial class CatalogJsonContext : JsonSerializerContext;

    internal static Catalog Read(string path)
        => AssetInput.ReadJson(path, CatalogJsonContext.Default.Catalog, 4096);

    internal static void Validate(Catalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Bunny)) throw new InvalidDataException("Bunny is required.");
        var bistro = new[] { catalog.BistroExterior, catalog.BistroInterior, catalog.BistroWine };
        if (bistro.Any(path => path is not null) && bistro.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Native content requires all three Bistro scenes.");
        foreach (var path in new[] { catalog.Bunny }.Concat(bistro.Where(path => path is not null))) {
            var parts = path?.Split('/');
            if (parts is not { Length: 3 } || parts[0] != "Content" || parts[1].Length != 64
                || parts[1].Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !parts[2].EndsWith(".siascene", StringComparison.Ordinal)
                || !Guid.TryParseExact(parts[2][..^9], "N", out var id) || id == Guid.Empty
                || parts[2][..^9] != id.ToString("N"))
                throw new InvalidDataException("Example scene paths require Content/<sha256>/<uuid>.siascene.");
        }
    }

    internal static string DefaultScene(ScenePipeline pipeline, string assetsDirectory)
    {
        var catalog = Read(Path.Combine(assetsDirectory, "default-scenes.json")); Validate(catalog);
        var relative = pipeline == ScenePipeline.Bunny ? catalog.Bunny : catalog.BistroExterior
            ?? throw new InvalidDataException("Bistro requires a native content package.");
        return Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static async Task PackAsync(string recipePath, string outputDirectory,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumBundleBytes = 512L * 1024 * 1024,
        bool browser = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBundleBytes);
        recipePath = Path.GetFullPath(recipePath); var recipe = Read(recipePath);
        var inputPaths = browser ? new[] { recipe.Bunny }
            : new[] { recipe.Bunny, recipe.BistroExterior, recipe.BistroInterior, recipe.BistroWine };
        if (inputPaths.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Every default scene needs an input.");
        var inputs = inputPaths.Select(path => Path.GetFullPath(path!, Path.GetDirectoryName(recipePath)!)).ToArray();
        if (inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
            throw new InvalidDataException("Default scene inputs must be distinct.");
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Content package requires an existing parent.");
        if (Path.Exists(output)) throw new IOException("Content package output must not already exist.");
        var stage = Path.Combine(parent, ".example-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var directories = new List<string> { stage }; var files = new List<string>();
        void RequireOwnedDirectories() {
            foreach (var directory in directories)
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Content package staging directories must not be links.");
        }
        using var client = new HttpClient();
        async Task<HashSet<string>> RequiredFiles(string scene) {
            var virtuals = await PbrSceneFiles.PrepareVirtualAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
            var ids = virtuals.Meshes.Keys.Concat(virtuals.Materials.Keys).Concat(virtuals.Textures.Keys)
                .Concat(virtuals.Products.Keys);
            if (!browser) {
                var raster = await PbrSceneFiles.PrepareRasterAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
                var bvh = await PbrSceneFiles.PrepareBvhAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
                ids = ids.Concat(raster.Products.Keys).Concat(bvh.Products.Keys);
            }
            var names = ids.Select(id => id.Value.ToString("N") + ".siaasset").ToHashSet(StringComparer.Ordinal);
            names.Add(virtuals.Id.Value.ToString("N") + ".siascene");
            return names;
        }
        try {
            RequireOwnedDirectories();
            var content = Path.Combine(stage, "Content"); Directory.CreateDirectory(content); directories.Add(content);
            var roots = new List<string>(inputs.Length);
            for (var index = 0; index < inputs.Length; index++) {
                cancellationToken.ThrowIfCancellationRequested();
                var input = inputs[index]; var required = await RequiredFiles(input);
                RequireOwnedDirectories();
                var folder = Path.Combine(content, "input-" + index); Directory.CreateDirectory(folder); directories.Add(folder);
                var copied = new List<string>(); long total = 0;
                var buffer = new byte[65536]; var encodedLength = new byte[8];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData("SIAEXAMPLECONTENT1"u8);
                foreach (var name in required.Order(StringComparer.Ordinal)) {
                    cancellationToken.ThrowIfCancellationRequested(); RequireOwnedDirectories();
                    var source = Path.Combine(Path.GetDirectoryName(input)!, name);
                    using var read = File.OpenRead(source);
                    var length = read.Length;
                    if (length > maximumFileBytes || length > maximumBundleBytes - total)
                        throw new InvalidDataException("Content package exceeds its encoded byte admission.");
                    total += length;
                    hash.AppendData(Encoding.UTF8.GetBytes(name));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(encodedLength, length);
                    hash.AppendData(encodedLength);
                    var target = Path.Combine(folder, name);
                    using var write = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    files.Add(target); copied.Add(target);
                    var remaining = length;
                    while (remaining != 0) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = read.Read(buffer, 0, (int)System.Math.Min(buffer.Length, remaining));
                        if (count == 0) throw new EndOfStreamException("Content package input changed while reading.");
                        hash.AppendData(buffer.AsSpan(0, count)); write.Write(buffer, 0, count); remaining -= count;
                    }
                    if (read.ReadByte() != -1) throw new IOException("Content package input grew while reading.");
                }
                var sceneName = Path.GetFileName(input);
                if (!required.SetEquals(await RequiredFiles(Path.Combine(folder, sceneName))))
                    throw new InvalidDataException("Content references changed during packaging.");
                var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                var version = Path.Combine(content, digest);
                RequireOwnedDirectories(); Directory.Move(folder, version);
                directories[directories.IndexOf(folder)] = version;
                foreach (var file in copied) files[files.IndexOf(file)] = Path.Combine(version, Path.GetFileName(file));
                roots.Add("Content/" + digest + "/" + sceneName);
            }
            var catalog = browser ? new Catalog { Bunny = roots[0] }
                : new Catalog { Bunny = roots[0], BistroExterior = roots[1], BistroInterior = roots[2], BistroWine = roots[3] };
            Validate(catalog); RequireOwnedDirectories();
            var descriptor = Path.Combine(stage, "default-scenes.json");
            using (var write = new FileStream(descriptor, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                files.Add(descriptor); JsonSerializer.Serialize(write, catalog, CatalogJsonContext.Default.Catalog);
            }
            cancellationToken.ThrowIfCancellationRequested(); RequireOwnedDirectories();
            OfflineDirectory.Publish(stage, output, cancellationToken);
        }
        catch (Exception error) {
            try {
                RequireOwnedDirectories();
                var knownFiles = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var knownDirectories = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
                        || !knownFiles.Contains(entry) && !knownDirectories.Contains(entry))
                        throw new IOException("Unknown or linked content package staging entry; preserve staging.");
                foreach (var file in files) File.Delete(file);
                foreach (var directory in directories.OrderByDescending(path => path.Length)) Directory.Delete(directory, recursive: false);
            }
            catch (Exception cleanup) { throw new AggregateException("Content package failed and cleanup was incomplete.", error, cleanup); }
            throw;
        }
    }
}
#endif
