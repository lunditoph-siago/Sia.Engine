#if !BROWSER
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Offline application layout/selection. Domain records and the asset library own no publication workflow.
internal static partial class ScenePack
{
    internal const int MaximumRecipeBytes = 1024 * 1024;

    internal sealed record Entry
    {
        public required Guid Id { get; init; }
        public required string Input { get; init; }
    }

    internal sealed record Recipe
    {
        public required Guid Scene { get; init; }
        public required string Input { get; init; }
        public bool Raster { get; init; } = true;
        public bool RasterPages { get; init; }
        public bool Virtual { get; init; }
        public bool Bvh { get; init; }
        public Entry[] Meshes { get; init; } = [];
        public Entry[] Materials { get; init; } = [];
        public Entry[] Textures { get; init; } = [];
        public LightingFiles.GltfLightInput? GltfLights { get; init; }
        public CameraEntry[]? Cameras { get; init; }
        public GltfCameraInput? GltfCameras { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 8)]
    [JsonSerializable(typeof(Recipe))]
    private partial class SceneRecipeJsonContext : JsonSerializerContext;

    internal static async Task<AssetId> PackAsync(string recipePath, string outputDirectory,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, LightmapCook.Recipe? lightmap = null,
        IReadOnlyDictionary<AssetId, MeshBakeLayout>? bakeLayouts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipePath); ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes); ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Scene output requires an existing parent.");
        if (Path.Exists(output)) throw new IOException("Scene output must not already exist.");
        recipePath = Path.GetFullPath(recipePath);
        var recipe = ReadRecipe(recipePath);
        var authoredLights = recipe.GltfLights is { } sourceLights
            ? await LightingFiles.ReadGltfLightsAsync(sourceLights, Path.GetDirectoryName(recipePath)!, maximumFileBytes, cancellationToken) : null;
        var authoredCameras = await ReadCamerasAsync(recipe, Path.GetDirectoryName(recipePath)!, maximumFileBytes, cancellationToken);
        var bakeSettings = lightmap?.Settings();
        if (lightmap is not null && bakeLayouts is null)
            throw new InvalidDataException("Lightmap scene publication requires exact canonical layouts.");
        var ids = new Dictionary<AssetId, Type>();
        void Claim(AssetId id, Type kind)
        {
            if (!id.IsValid || ids.TryGetValue(id, out var existing) && existing != kind)
                throw new InvalidDataException("Scene asset IDs must be nonempty and have one record kind.");
            ids[id] = kind;
        }
        Dictionary<AssetId, string> Entries(Entry[] entries, Type kind)
        {
            if (entries is null || entries.Length > 4096) throw new InvalidDataException("Invalid scene recipe entry table.");
            var result = new Dictionary<AssetId, string>();
            foreach (var entry in entries) {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Input)) throw new InvalidDataException("Scene entry requires an input path.");
                var id = new AssetId(entry.Id); Claim(id, kind);
                if (!result.TryAdd(id, Path.GetFullPath(entry.Input, Path.GetDirectoryName(recipePath)!)))
                    throw new InvalidDataException("Scene recipe entry IDs must be unique.");
            }
            return result;
        }
        if (!recipe.Raster && !recipe.RasterPages && !recipe.Virtual && !recipe.Bvh)
            throw new InvalidDataException("Select at least one of Raster, RasterPages, Virtual or BVH.");
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Input);
        var sceneId = new AssetId(recipe.Scene); Claim(sceneId, typeof(PbrSceneRecord));
        var meshInputs = Entries(recipe.Meshes, typeof(MeshManifestRecord));
        var materialInputs = Entries(recipe.Materials, typeof(PbrMaterialRecord));
        var textureInputs = Entries(recipe.Textures, typeof(PbrTextureRecord));
        IblEnvironmentRecord? environment = null;
        if (lightmap?.Environment is { } environmentInput) {
            Claim(new(environmentInput.Id), typeof(IblEnvironmentRecord));
            if (IblEnvironmentAsset.EncodedBytes > maximumFileBytes || IblEnvironmentAsset.EncodedBytes > maximumTotalBytes
                || 4ul * IblEnvironmentAsset.EncodedBytes + 8192 > lightmap.MaximumWorkingBytes)
                throw new InvalidDataException("Lightmap environment exceeds encoded or preparation working admission.");
            using var http = new HttpClient();
            environment = (await LightingFiles.PrepareAsync(Path.GetFullPath(environmentInput.Input, Path.GetDirectoryName(recipePath)!),
                null, http, cancellationToken)).Environment;
            bakeSettings = lightmap.Settings(environment);
        }
        var stage = Path.Combine(parent, ".scene-pack-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(stage)) throw new IOException("Scene staging path already exists.");
        Directory.CreateDirectory(stage);
        var files = new List<string>(); long total = 0;
        var lightingDirectory = Path.Combine(stage, ".lighting");
        IReadOnlyList<string>? lightingFiles = null;
        void RequireStage()
        {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scene staging directory must not be a directory link.");
        }
        try {
            async Task Publish(AssetId id, ReadOnlyMemory<byte> bytes, bool scene)
            {
                if (bytes.Length > maximumFileBytes || bytes.Length > maximumTotalBytes - total)
                    throw new InvalidDataException("Scene output exceeds its encoded publication admission.");
                cancellationToken.ThrowIfCancellationRequested(); RequireStage(); total += bytes.Length;
                var target = Path.Combine(stage, id.Value.ToString("N") + (scene ? ".siascene" : ".siaasset"));
                using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                files.Add(target); await file.WriteAsync(bytes, cancellationToken);
            }
            async Task<T> Read<T>(AssetId id, string path, Func<Stream, string, T> decode, bool scene = false, bool publish = true,
                int? maximumEncodedBytes = null) where T : class, IAssetRecord
            {
                cancellationToken.ThrowIfCancellationRequested(); Claim(id, typeof(T));
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                // Offline-only BVHs have the same per-file decoder bound but do not spend runtime package admission.
                var available = publish ? (int)System.Math.Min(maximumFileBytes, maximumTotalBytes - total) : maximumFileBytes;
                if (maximumEncodedBytes is { } encodedLimit) available = System.Math.Min(available, encodedLimit);
                if (input.Length > available)
                    throw new InvalidDataException($"Scene input {id.Value:N} requires {input.Length} encoded bytes; remaining admission {available}, consumed {total}, total limit {maximumTotalBytes}.");
                var bytes = await AssetInput.ReadBoundedAsync(input, available, cancellationToken);
                if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected encoded array.");
                T record;
                using (var payload = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)) record = decode(payload, path);
                cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                if (!publish) return record;
                await Publish(id, bytes, scene);
                return record;
            }
            string Locate(Dictionary<AssetId, string> inputs, AssetId id) => inputs.TryGetValue(id, out var path)
                ? path : throw new AssetNotFoundException("Scene recipe has no entry for a required asset.");
            var scene = await Read(sceneId, Path.GetFullPath(recipe.Input, Path.GetDirectoryName(recipePath)!),
                (s, n) => PbrSceneRecord.Load(s, (ulong)maximumFileBytes, n), scene: true, publish: authoredLights is null && authoredCameras is null);
            if (authoredLights is not null || authoredCameras is not null) {
                var data = scene.Data;
                if (authoredLights is not null) data = data.WithLights(authoredLights);
                if (authoredCameras is not null) data = data.WithCameras(authoredCameras);
                scene = scene with { Data = data };
                if (scene.Data.EncodedBytes > (ulong)maximumFileBytes || scene.Data.MetadataArrayBytes > (ulong)maximumFileBytes
                    || scene.Data.EncodedBytes > (ulong)(maximumTotalBytes - total))
                    throw new InvalidDataException("Authored metadata exceeds scene publication admission.");
                var bytes = new byte[checked((int)scene.Data.EncodedBytes)];
                using (var encoded = new MemoryStream(bytes, writable: true)) scene.Data.Save(encoded, cancellationToken);
                await Publish(sceneId, bytes, scene: true);
            }
            var meshes = new Dictionary<AssetId, MeshManifestRecord>();
            var rasters = new Dictionary<AssetId, MeshRasterRecord>(); var virtuals = new Dictionary<AssetId, MeshVirtualRecord>();
            var pagedRasters = new Dictionary<AssetId, MeshRasterPagesRecord>();
            var publishedRasterPages = new HashSet<AssetId>();
            var bvhs = new Dictionary<AssetId, MeshBvhRecord>();
            var virtualPages = new Dictionary<AssetId, MeshVirtualPageRecord>();
            var materials = new Dictionary<AssetId, PbrMaterialRecord>(); var textures = new Dictionary<AssetId, PbrTextureRecord>();
            var used = new bool[scene.Data.Bindings.Length]; foreach (var instance in scene.Data.Instances) used[instance.Binding] = true;
            var bakeMeshes = new HashSet<AssetId>();
            if (lightmap is not null) {
                _ = scene.Data.SelectInstances(lightmap.StaticInstances, cancellationToken);
                var staticIds = lightmap.StaticInstances.ToHashSet();
                foreach (var instance in scene.Data.Instances) if (staticIds.Contains(instance.Id))
                    bakeMeshes.Add(scene.Data.Bindings[instance.Binding].Mesh.Value);
            }
            for (var bindingIndex = 0; bindingIndex < used.Length; bindingIndex++) {
                cancellationToken.ThrowIfCancellationRequested(); if (!used[bindingIndex]) continue;
                var binding = scene.Data.Bindings[bindingIndex]; var meshId = binding.Mesh.Value;
                if (!meshes.TryGetValue(meshId, out var mesh)) {
                    var path = Locate(meshInputs, meshId);
                    mesh = await Read(meshId, path, MeshManifestRecord.Load); meshes.Add(meshId, mesh);
                    Claim(mesh.Data.Source, typeof(MeshSourceRecord));
                    if (mesh.Data.Raster is { } rasterReference) Claim(rasterReference.Id, typeof(MeshRasterRecord));
                    if (mesh.Data.RasterPages is { } pagedReference) Claim(pagedReference.Id, typeof(MeshRasterPagesRecord));
                    if (mesh.Data.Virtual is { } virtualReference) Claim(virtualReference.Id, typeof(MeshVirtualRecord));
                    if (mesh.Data.Bvh is { } bvhReference) Claim(bvhReference.Id, typeof(MeshBvhRecord));
                    string Product(AssetId id) => Path.Combine(Path.GetDirectoryName(path)!, id.Value.ToString("N") + ".siaasset");
                    if (recipe.Raster) {
                        var id = mesh.Data.Raster?.Id ?? throw new InvalidDataException("Selected Mesh has no Raster product.");
                        if (!rasters.ContainsKey(id)) rasters.Add(id, await Read(id, Product(id), (s, n) => MeshRasterRecord.Load(s, (ulong)maximumFileBytes, n)));
                    }
                    if (recipe.RasterPages) {
                        var id = mesh.Data.RasterPages?.Id ?? throw new InvalidDataException("Selected Mesh has no paged Raster product.");
                        if (!pagedRasters.ContainsKey(id)) {
                            var index = await Read(id, Product(id), (s, n) => MeshRasterPagesRecord.Load(s, (ulong)maximumFileBytes, n));
                            if (index.Data.ProductId != id) throw new InvalidAssetException("Raster page index belongs to another logical product.");
                            pagedRasters.Add(id, index);
                            for (var pageIndex = 0; pageIndex < index.Data.Pages.Length; pageIndex++) {
                                var page = index.Data.Pages[pageIndex];
                                var record = await Read(page.Id, Product(page.Id),
                                    (s, n) => MeshRasterPageRecord.Load(s, (ulong)maximumFileBytes, n),
                                    publish: publishedRasterPages.Add(page.Id), maximumEncodedBytes: MeshRasterPageData.MaximumEncodedBytes);
                                index.Data.ValidatePage(pageIndex, record.Data, cancellationToken);
                            }
                        }
                    }
                    if (recipe.Virtual) {
                        var id = mesh.Data.Virtual?.Id ?? throw new InvalidDataException("Selected Mesh has no Virtual product.");
                        if (!virtuals.TryGetValue(id, out var virtualRecord)) {
                            virtualRecord = await Read(id, Product(id), (s, n) => MeshVirtualRecord.Load(s, (ulong)maximumFileBytes, n));
                            if (virtualRecord.Data.ProductId != id) throw new InvalidAssetException("Virtual index belongs to another logical product.");
                            virtuals.Add(id, virtualRecord);
                            for (var p = 0; p < virtualRecord.Data.Pages.Length; p++) {
                                var page = virtualRecord.Data.Pages[p];
                                if (!virtualPages.TryGetValue(page.Id, out var pageRecord)) {
                                    pageRecord = await Read(page.Id, Product(page.Id), (s, n) => MeshVirtualPageRecord.Load(s, (ulong)MeshVirtualPageData.MaximumEncodedBytes, n));
                                    virtualPages.Add(page.Id, pageRecord);
                                }
                                virtualRecord.Data.ValidatePage(p, pageRecord.Data, cancellationToken);
                            }
                        }
                    }
                    if (recipe.Bvh || bakeMeshes.Contains(meshId)) {
                        var id = mesh.Data.Bvh?.Id ?? throw new InvalidDataException("Selected Mesh has no BVH product.");
                        if (!bvhs.ContainsKey(id)) bvhs.Add(id, await Read(id, Product(id), (s, n) => MeshBvhRecord.Load(s, (ulong)maximumFileBytes, n), publish: recipe.Bvh));
                    }
                }
                for (var slotIndex = 0; slotIndex < binding.Materials.Length; slotIndex++) {
                    var slot = binding.Materials[slotIndex];
                    var id = slot.Material.Value;
                    if (materials.ContainsKey(id)) continue;
                    var material = await Read(id, Locate(materialInputs, id), PbrMaterialRecord.Load); materials.Add(id, material);
                    foreach (var reference in material.Data.TextureReferences) {
                        if (!textures.ContainsKey(reference.Value)) textures.Add(reference.Value, await Read(reference.Value,
                            Locate(textureInputs, reference.Value), (s, n) => PbrTextureRecord.Load(s, (ulong)maximumFileBytes, n)));
                    }
                }
            }
            // Detail products are explicit package members, but never retained as scene-wide decoded textures.
            var textureChunks = new HashSet<string>(StringComparer.Ordinal);
            using (var client = new HttpClient())
            await using (var source = new PbrSceneFiles.PageSource(Path.Combine(stage, sceneId.Value.ToString("N") + ".siascene"), client,
                new PbrSceneFiles.Prepared<MeshVirtualRecord>(sceneId, scene, meshes, virtuals, materials, textures))) {
                // Authenticate copied chunks and decode against the staged root before publication.
                foreach (var pair in textures) {
                    if (pair.Value.Mips is not { } index) continue;
                    var directory = Path.GetDirectoryName(Locate(textureInputs, pair.Key))!;
                    for (var mip = 0; mip < index.TailMip; mip++) {
                        var reference = index.Mips[mip]; Claim(reference.Id, typeof(PbrTextureRecord));
                        if (reference.EncodedBytes > maximumFileBytes)
                            throw new InvalidDataException("Texture detail exceeds its independent decode limit.");
                        foreach (var chunk in reference.Content.Chunks) {
                            if (!textureChunks.Add(chunk.Id)) continue;
                            if (chunk.Length > maximumFileBytes || chunk.Length > maximumTotalBytes - total)
                                throw new InvalidDataException("Texture detail chunks exceed scene publication admission.");
                            total += chunk.Length; cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                            using var input = File.OpenRead(Path.Combine(directory, chunk.FileName));
                            if (input.Length != chunk.Length) throw new InvalidDataException("Texture detail chunk length mismatch.");
                            var target = Path.Combine(stage, chunk.FileName);
                            using (var outputFile = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                                files.Add(target);
                                var buffer = new byte[65536]; var remaining = chunk.Length;
                                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                                while (remaining != 0) {
                                    var count = await input.ReadAsync(buffer.AsMemory(0, System.Math.Min(buffer.Length, remaining)), cancellationToken);
                                    if (count == 0) throw new InvalidDataException("Truncated texture detail chunk.");
                                    hash.AppendData(buffer.AsSpan(0, count)); await outputFile.WriteAsync(buffer.AsMemory(0, count), cancellationToken); remaining -= count;
                                }
                                if (input.ReadByte() != -1 || Convert.ToHexString(hash.GetHashAndReset()) != chunk.Id)
                                    throw new InvalidDataException("Texture detail chunk differs from its declared content.");
                            }
                        }
                        index.ValidateMip(mip, await source.ReadMipAsync(reference, cancellationToken));
                    }
                }
            }
            // Original generated acquisition plus pure domain composition validate selected file graphs.
            using (var world = new World()) {
                world.AddAddon<AssetLibrary>();
                if (recipe.RasterPages) PbrSceneComposition.ResolveRasterPages(scene.Data,
                    r => meshes[r.Value].Data, r => pagedRasters[r.Value].Data,
                    r => materials[r.Value].Data, r => textures[r.Value].Data);
                if (scene.Data.HasAuthoredLights) LightingFiles.PublishSceneLights(world, scene.Data, null);
                if (recipe.Raster) PbrSceneFiles.AcquireRaster(world, new(sceneId, scene, meshes, rasters, materials, textures)).Root.Destroy();
                if (recipe.Virtual) {
                    AssetLibrary.RegisterAsset<MeshVirtualPageAsset, MeshVirtualPageRecord>();
                    var use = PbrSceneFiles.AcquireVirtual(world, new(sceneId, scene, meshes, virtuals, materials, textures));
                    try { foreach (var page in virtualPages) world.AcquireAsset(page.Key, () => page.Value, use.Root); }
                    finally { use.Root.Destroy(); }
                }
                if (recipe.Bvh) PbrSceneFiles.AcquireBvh(world, new(sceneId, scene, meshes, bvhs, materials, textures)).Root.Destroy();
            }
            if (lightmap is not null) {
                if (ids.ContainsKey(new(lightmap.Product))) throw new InvalidDataException("Lightmap product aliases a scene input identity.");
                var lighting = LightmapCook.Cook(new(sceneId, scene, meshes, bvhs, materials, textures), lightmap.StaticInstances,
                    bakeLayouts!, new(lightmap.Product), lightingDirectory, bakeSettings,
                    maximumMetadataBytes: System.Math.Min((ulong)maximumFileBytes, PbrLightmapData.MaximumEncodedBytes),
                    maximumWorkingBytes: lightmap.MaximumWorkingBytes, maximumStagingBytes: lightmap.MaximumStagingBytes,
                    cancellationToken: cancellationToken, maximumEncodedBytes: (ulong)(maximumTotalBytes - total),
                    maximumFileBytes: (ulong)maximumFileBytes, maximumPageResolution: lightmap.MaximumPageResolution,
                    environment: environment);
                lightingFiles = lighting.Files;
                foreach (var filename in lightingFiles) {
                    cancellationToken.ThrowIfCancellationRequested(); var path = Path.Combine(lightingDirectory, filename);
                    var id = new AssetId(Guid.ParseExact(Path.GetFileNameWithoutExtension(filename), "N"));
                    if (ids.ContainsKey(id) && (bakeSettings?.Environment?.Id != id || ids[id] != typeof(IblEnvironmentRecord)))
                        throw new InvalidDataException("Lighting output aliases a scene input identity.");
                    var length = new FileInfo(path).Length;
                    if (length > maximumFileBytes || length > maximumTotalBytes - total)
                        throw new InvalidDataException("Lighting files exceed selected scene publication admission.");
                    total += length;
                }
                foreach (var filename in lightingFiles) {
                    cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                    if ((File.GetAttributes(lightingDirectory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Lightmap publication directory became a link.");
                    var source = Path.Combine(lightingDirectory, filename); var target = Path.Combine(stage, filename);
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Lightmap publication file became a link.");
                    File.Move(source, target, overwrite: false); files.Add(target);
                }
                Directory.Delete(lightingDirectory, recursive: false); lightingFiles = null;
            }
            cancellationToken.ThrowIfCancellationRequested(); RequireStage();
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return sceneId;
        }
        catch (Exception error) {
            try { RequireStage(); }
            catch (Exception cleanup) { throw new AggregateException("Scene packing failed; staging could not be verified and was preserved.", error, cleanup); }
            var failures = new List<Exception>();
            if (lightingFiles is not null) {
                try {
                    if ((File.GetAttributes(lightingDirectory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Lightmap publication link was preserved.");
                    foreach (var filename in lightingFiles) {
                        var path = Path.Combine(lightingDirectory, filename);
                        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Lightmap publication file link was preserved.");
                        File.Delete(path);
                    }
                    Directory.Delete(lightingDirectory, recursive: false);
                }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            foreach (var path in files) {
                try {
                    if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Scene publication file link was preserved.");
                    File.Delete(path);
                }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            try { Directory.Delete(stage, recursive: false); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count != 0) throw new AggregateException("Scene packing failed and staging cleanup was incomplete.", [error, .. failures]);
            throw;
        }
    }

    private static Recipe ReadRecipe(string path)
        => AssetInput.ReadJson(path, SceneRecipeJsonContext.Default.Recipe, MaximumRecipeBytes);
}
#endif
