#if !BROWSER
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class ExampleContent
{
    // Each entry selects a cooked scene and optional independent lighting, relative to the recipe.
    internal static async Task PackAsync(string recipePath, string outputDirectory,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumBundleBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, bool browser = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBundleBytes);
        recipePath = Path.GetFullPath(recipePath); var recipe = Read(recipePath);
        var bunny = recipe.Bunny ?? throw new InvalidDataException("Bunny is required.");
        var selected = browser ? new Entry { Scene = bunny.Scene } : bunny;
        if (string.IsNullOrWhiteSpace(selected.Scene)
            || selected.Lightmaps is not null && string.IsNullOrWhiteSpace(selected.Lightmaps)
            || selected.Environment is not null && string.IsNullOrWhiteSpace(selected.Environment)
            || selected.ReflectionCaptures is null || selected.ReflectionCaptures.Length > 16
            || selected.ReflectionCaptures.Any(string.IsNullOrWhiteSpace)
            || selected.ReflectionCaptures.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.ReflectionCaptures.Length)
            throw new InvalidDataException("Bunny requires nonempty selected input paths.");
        string Resolve(string path) => Path.GetFullPath(path, Path.GetDirectoryName(recipePath)!);
        var input = MapPaths(selected, Resolve);
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
        async Task AuthenticateFile(string directory, AssetId id, string expectedHash, int expectedBytes) {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = File.OpenRead(Path.Combine(directory, id.Value.ToString("N") + ".siaasset"));
            if (file.Length != expectedBytes || file.Length > maximumFileBytes)
                throw new InvalidDataException("Selected record differs from its declared encoded length.");
            var digest = await SHA256.HashDataAsync(file, cancellationToken);
            if (file.Length != expectedBytes || Convert.ToHexString(digest) != expectedHash)
                throw new InvalidDataException("Selected record differs from its declared content hash.");
        }
        async Task<Dictionary<string, string>> RequiredFiles(Entry entry) {
            var scene = entry.Scene;
            var raster = await PbrSceneFiles.PrepareRasterAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
            var virtuals = await PbrSceneFiles.PrepareVirtualAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
            var bvh = await PbrSceneFiles.PrepareBvhAsync(scene, client, maximumFileBytes, maximumBundleBytes, cancellationToken);
            await using (var pageSource = new PbrSceneFiles.PageSource(scene, client, virtuals)) {
                long total = virtuals.EncodedBytes;
                foreach (var product in virtuals.Products.Values) {
                    for (var p = 0; p < product.Data.Pages.Length; p++) {
                        var page = product.Data.Pages[p];
                        if (page.EncodedBytes > maximumFileBytes || page.EncodedBytes > maximumBundleBytes - total)
                            throw new InvalidDataException("Virtual pages exceed the package encoded admission.");
                        total += page.EncodedBytes;
                        // Runtime content sharing does not authenticate every independently named file in this snapshot.
                        await AuthenticateFile(Path.GetDirectoryName(scene)!, page.Id, page.ContentHash, page.EncodedBytes);
                        _ = await pageSource.ReadAsync(page.Reference, cancellationToken);
                    }
                }
            }
            var ids = raster.Meshes.Keys.Concat(raster.Materials.Keys).Concat(raster.Textures.Keys)
                .Concat(raster.Products.Keys).Concat(virtuals.Products.Keys).Concat(bvh.Products.Keys)
                .Concat(virtuals.Products.Values.SelectMany(p => p.Data.Pages.ToArray()).Select(p => p.Id));
            var names = ids.Select(id => id.Value.ToString("N") + ".siaasset").Append(raster.Id.Value.ToString("N") + ".siascene")
                .Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => Path.Combine(Path.GetDirectoryName(scene)!, name), StringComparer.Ordinal);
            long encoded = 0;
            void Admit(long bytes) {
                if (bytes < 0 || bytes > maximumFileBytes || bytes > maximumBundleBytes - encoded)
                    throw new InvalidDataException("Selected content exceeds the package encoded admission.");
                encoded += bytes;
            }
            foreach (var file in names.Values) { cancellationToken.ThrowIfCancellationRequested(); Admit(new FileInfo(file).Length); }
            if (entry.Lightmaps is { } lightmaps) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lightmaps.EndsWith(".siaasset", StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(lightmaps), "N", out var lightingId)
                    || lightingId == Guid.Empty)
                    throw new InvalidDataException("Lighting set requires its nonempty UUID and .siaasset suffix.");
                if (new FileInfo(lightmaps).Length > System.Math.Min(maximumFileBytes, PbrLightmapSetData.MaximumEncodedBytes))
                    throw new InvalidDataException("Lighting set exceeds the per-file admission.");
                Admit(new FileInfo(lightmaps).Length);
                var lighting = FileLoader.Load<PbrLightmapSetRecord, ulong>(lightmaps, PbrLightmapSetData.MaximumPayloadBytes);
                var set = lighting.Data;
                if (set.ProductId.Value != lightingId) throw new InvalidDataException("Lighting set belongs to another logical ID.");
                set.ValidateScene(virtuals.Scene.Data, r => virtuals.Meshes[r.Value].Data,
                    r => virtuals.Materials[r.Value].Data, r => virtuals.Textures[r.Value].Data, cancellationToken);
                await using var lightingSource = new LightmapFiles.ProductSource(lightmaps, client, set,
                    32L * 1024 * 1024, otherIds: ids.Concat(raster.Meshes.Values.Select(mesh => mesh.Data.Source)).Append(raster.Id));
                void Add(string name) {
                    if (!names.TryAdd(name, Path.Combine(Path.GetDirectoryName(lightmaps)!, name)))
                        throw new InvalidDataException("Lighting aliases a selected scene record identity.");
                }
                Add(set.ProductId.Value.ToString("N") + ".siaasset");
                // Verify one independent member at a time. Root size and the
                // decoded reservation never accumulate scene-wide chart arrays.
                foreach (var reference in set.Products.ToArray()) {
                    cancellationToken.ThrowIfCancellationRequested();
                    Admit(reference.EncodedBytes);
                    await AuthenticateFile(Path.GetDirectoryName(lightmaps)!, reference.Id, reference.ContentHash, reference.EncodedBytes);
                    var member = await lightingSource.ReadAsync(reference.Reference, cancellationToken);
                    Add(reference.Id.Value.ToString("N") + ".siaasset");
                    for (var at = 0; at < member.Data.Pages.Length; at++) {
                        var page = member.Data.Pages[at];
                        Admit(page.EncodedBytes);
                        await AuthenticateFile(Path.GetDirectoryName(lightmaps)!, page.Id, page.ContentHash, page.EncodedBytes);
                        _ = await lightingSource.ReadAsync(member.Data, at, cancellationToken);
                        Add(page.Id.Value.ToString("N") + ".siaasset");
                    }
                }
            }
            await AddReflectionsAsync(entry, raster, client, names, Admit, maximumFileBytes, maximumBundleBytes - encoded, cancellationToken);
            return names;
        }
        try {
            RequireOwnedDirectories();
            var content = Path.Combine(stage, "Content"); Directory.CreateDirectory(content); directories.Add(content);
            cancellationToken.ThrowIfCancellationRequested();
            var required = await RequiredFiles(input);
            RequireOwnedDirectories();
            var folder = Path.Combine(content, "bunny"); Directory.CreateDirectory(folder); directories.Add(folder);
            var copied = new List<string>(); long total = 0;
            var buffer = new byte[65536]; var encodedLength = new byte[8];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("SIAEXAMPLECONTENT1"u8);
            foreach (var name in required.Keys.Order(StringComparer.Ordinal)) {
                cancellationToken.ThrowIfCancellationRequested(); RequireOwnedDirectories();
                var source = required[name];
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
            // Validate the owned snapshot, including cross-record build keys and every selected product.
            var snapshot = MapPaths(input, path => Path.Combine(folder, Path.GetFileName(path)));
            if (!required.Keys.ToHashSet(StringComparer.Ordinal).SetEquals((await RequiredFiles(snapshot)).Keys))
                throw new InvalidDataException("Content references changed during packaging.");
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            var version = Path.Combine(content, digest);
            RequireOwnedDirectories(); Directory.Move(folder, version);
            directories[directories.IndexOf(folder)] = version;
            foreach (var file in copied) files[files.IndexOf(file)] = Path.Combine(version, Path.GetFileName(file));
            var catalog = new Catalog { Bunny = MapPaths(input, path => "Content/" + digest + "/" + Path.GetFileName(path)) };
            Validate(catalog); RequireOwnedDirectories();
            var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog, CatalogJsonContext.Default.Catalog);
            if (catalogBytes.Length > 16384) throw new InvalidDataException("Example content catalog exceeds 16 KiB.");
            var descriptor = Path.Combine(stage, "default-scenes.json");
            using (var write = new FileStream(descriptor, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                files.Add(descriptor); write.Write(catalogBytes);
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
