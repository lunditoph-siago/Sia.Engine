#if !BROWSER
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
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
        public bool Virtual { get; init; }
        public bool Bvh { get; init; }
        public Entry[] Meshes { get; init; } = [];
        public Entry[] Materials { get; init; } = [];
        public Entry[] Textures { get; init; } = [];
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 8)]
    [JsonSerializable(typeof(Recipe))]
    private partial class SceneRecipeJsonContext : JsonSerializerContext;

    internal static async Task<AssetId> PackAsync(string recipePath, string outputDirectory,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
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
        if (!recipe.Raster && !recipe.Virtual && !recipe.Bvh) throw new InvalidDataException("Select at least one of Raster, Virtual or BVH.");
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Input);
        var sceneId = new AssetId(recipe.Scene); Claim(sceneId, typeof(PbrSceneRecord));
        var meshInputs = Entries(recipe.Meshes, typeof(MeshManifestRecord));
        var materialInputs = Entries(recipe.Materials, typeof(PbrMaterialRecord));
        var textureInputs = Entries(recipe.Textures, typeof(PbrTextureRecord));
        var stage = Path.Combine(parent, ".scene-pack-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(stage)) throw new IOException("Scene staging path already exists.");
        Directory.CreateDirectory(stage);
        var files = new List<string>(); long total = 0;
        void RequireStage()
        {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scene staging directory must not be a directory link.");
        }
        try {
            async Task<T> Read<T>(AssetId id, string path, Func<Stream, string, T> decode, bool scene = false) where T : class, IAssetRecord
            {
                cancellationToken.ThrowIfCancellationRequested(); Claim(id, typeof(T));
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var available = (int)System.Math.Min(maximumFileBytes, maximumTotalBytes - total);
                if (input.Length > available)
                    throw new InvalidDataException($"Scene input {id.Value:N} requires {input.Length} encoded bytes; remaining admission {available}, consumed {total}, total limit {maximumTotalBytes}.");
                var bytes = await AssetInput.ReadBoundedAsync(input, available, cancellationToken);
                total += bytes.Length;
                if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected encoded array.");
                T record;
                using (var payload = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)) record = decode(payload, path);
                cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                var target = Path.Combine(stage, id.Value.ToString("N") + (scene ? ".siascene" : ".siaasset"));
                using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                files.Add(target); await file.WriteAsync(bytes, cancellationToken);
                return record;
            }
            string Locate(Dictionary<AssetId, string> inputs, AssetId id) => inputs.TryGetValue(id, out var path)
                ? path : throw new AssetNotFoundException("Scene recipe has no entry for a required asset.");
            var scene = await Read(sceneId, Path.GetFullPath(recipe.Input, Path.GetDirectoryName(recipePath)!),
                (s, n) => PbrSceneRecord.Load(s, (ulong)maximumFileBytes, n), scene: true);
            var meshes = new Dictionary<AssetId, MeshManifestRecord>();
            var rasters = new Dictionary<AssetId, MeshRasterRecord>(); var virtuals = new Dictionary<AssetId, MeshVirtualRecord>();
            var bvhs = new Dictionary<AssetId, MeshBvhRecord>();
            var materials = new Dictionary<AssetId, PbrMaterialRecord>(); var textures = new Dictionary<AssetId, PbrTextureRecord>();
            var used = new bool[scene.Data.Bindings.Length]; foreach (var instance in scene.Data.Instances) used[instance.Binding] = true;
            for (var bindingIndex = 0; bindingIndex < used.Length; bindingIndex++) {
                cancellationToken.ThrowIfCancellationRequested(); if (!used[bindingIndex]) continue;
                var binding = scene.Data.Bindings[bindingIndex]; var meshId = binding.Mesh.Value;
                if (!meshes.TryGetValue(meshId, out var mesh)) {
                    var path = Locate(meshInputs, meshId);
                    mesh = await Read(meshId, path, MeshManifestRecord.Load); meshes.Add(meshId, mesh);
                    Claim(mesh.Data.Source, typeof(MeshSourceRecord));
                    if (mesh.Data.Raster is { } rasterReference) Claim(rasterReference.Id, typeof(MeshRasterRecord));
                    if (mesh.Data.Virtual is { } virtualReference) Claim(virtualReference.Id, typeof(MeshVirtualRecord));
                    if (mesh.Data.Bvh is { } bvhReference) Claim(bvhReference.Id, typeof(MeshBvhRecord));
                    string Product(AssetId id) => Path.Combine(Path.GetDirectoryName(path)!, id.Value.ToString("N") + ".siaasset");
                    if (recipe.Raster) {
                        var id = mesh.Data.Raster?.Id ?? throw new InvalidDataException("Selected Mesh has no Raster product.");
                        if (!rasters.ContainsKey(id)) rasters.Add(id, await Read(id, Product(id), (s, n) => MeshRasterRecord.Load(s, (ulong)maximumFileBytes, n)));
                    }
                    if (recipe.Virtual) {
                        var id = mesh.Data.Virtual?.Id ?? throw new InvalidDataException("Selected Mesh has no Virtual product.");
                        if (!virtuals.ContainsKey(id)) virtuals.Add(id, await Read(id, Product(id), (s, n) => MeshVirtualRecord.Load(s, (ulong)maximumFileBytes, n)));
                    }
                    if (recipe.Bvh) {
                        var id = mesh.Data.Bvh?.Id ?? throw new InvalidDataException("Selected Mesh has no BVH product.");
                        if (!bvhs.ContainsKey(id)) bvhs.Add(id, await Read(id, Product(id), (s, n) => MeshBvhRecord.Load(s, (ulong)maximumFileBytes, n)));
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
            // Original generated acquisition plus pure domain composition validate selected file graphs.
            using (var world = new World()) {
                world.AddAddon<AssetLibrary>();
                if (recipe.Raster) PbrSceneFiles.AcquireRaster(world, new(sceneId, scene, meshes, rasters, materials, textures)).Root.Destroy();
                if (recipe.Virtual) PbrSceneFiles.AcquireVirtual(world, new(sceneId, scene, meshes, virtuals, materials, textures)).Root.Destroy();
                if (recipe.Bvh) PbrSceneFiles.AcquireBvh(world, new(sceneId, scene, meshes, bvhs, materials, textures)).Root.Destroy();
            }
            cancellationToken.ThrowIfCancellationRequested(); RequireStage();
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return sceneId;
        }
        catch (Exception error) {
            try { RequireStage(); }
            catch (Exception cleanup) { throw new AggregateException("Scene packing failed; staging could not be verified and was preserved.", error, cleanup); }
            var failures = new List<Exception>();
            foreach (var path in files) { try { File.Delete(path); } catch (Exception cleanup) { failures.Add(cleanup); } }
            try { Directory.Delete(stage, recursive: false); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count != 0) throw new AggregateException("Scene packing failed and staging cleanup was incomplete.", [error, .. failures]);
            throw;
        }
    }

    private static Recipe ReadRecipe(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumRecipeBytes) throw new InvalidDataException("Scene recipe exceeds 1 MiB.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Scene recipe changed while reading.");
        return JsonSerializer.Deserialize(bytes, SceneRecipeJsonContext.Default.Recipe)
            ?? throw new InvalidDataException("Scene recipe must be an object.");
    }
}
#endif
