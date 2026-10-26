#if !BROWSER
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Example;

internal static partial class ContentCook
{
    internal const int MaximumRecipeBytes = 1024 * 1024;

    internal sealed record Recipe
    {
        public required ScenePack.Recipe Scene { get; init; }
        public required string[] Meshes { get; init; }
        public LightmapCook.Recipe? Lightmap { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 12)]
    [JsonSerializable(typeof(Recipe), TypeInfoPropertyName = "ContentRecipe")]
    [JsonSerializable(typeof(ScenePack.Recipe), TypeInfoPropertyName = "SceneRecipe")]
    private partial class ContentRecipeJsonContext : JsonSerializerContext;

    internal static async Task<AssetId> CookAsync(string recipePath, string outputDirectory,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, string? mikkToolPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipePath); ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes); ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        recipePath = Path.GetFullPath(recipePath);
        var recipe = ReadRecipe(recipePath);
        if (recipe.Scene is null || recipe.Meshes is null || recipe.Meshes.Length is < 1 or > 4096
            || recipe.Scene.Meshes is null || recipe.Scene.Meshes.Length != 0)
            throw new InvalidDataException("Content recipe requires 1..4096 Mesh recipes and no prebuilt scene Mesh entries.");
        if (!recipe.Scene.Raster && !recipe.Scene.Virtual && !recipe.Scene.Bvh)
            throw new InvalidDataException("Select at least one scene representation.");
        if (recipe.Lightmap is { } lightmap) {
            _ = lightmap.Settings();
        }
        var sceneId = new AssetId(recipe.Scene.Scene);
        var sourceDirectory = Path.GetDirectoryName(recipePath)!;
        string Locate(string path) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Path.GetFullPath(path, sourceDirectory);
        }
        ScenePack.Entry[] LocateEntries(ScenePack.Entry[] entries) {
            if (entries is null || entries.Length > 4096) throw new InvalidDataException("Invalid content entry table.");
            return entries.Select(entry => entry is null ? throw new InvalidDataException("Null content entry.")
                : entry with { Input = Locate(entry.Input) }).ToArray();
        }
        var scene = recipe.Scene with { Input = Locate(recipe.Scene.Input),
            Materials = LocateEntries(recipe.Scene.Materials), Textures = LocateEntries(recipe.Scene.Textures),
            GltfLights = recipe.Scene.GltfLights is { } lights ? lights with { Input = Locate(lights.Input) } : null,
            GltfCameras = recipe.Scene.GltfCameras is { } cameras ? cameras with { Input = Locate(cameras.Input) } : null };
        var meshInputs = new (AssetId Id, string Path, HashSet<string> Files)[recipe.Meshes.Length];
        var claimed = new HashSet<AssetId>();
        for (var index = 0; index < meshInputs.Length; index++) {
            var path = Locate(recipe.Meshes[index]);
            var mesh = MeshCook.ReadRecipe(path);
            var id = new AssetId(mesh.Mesh);
            if (!claimed.Add(id)) throw new InvalidDataException("Content Mesh identities must be unique.");
            Guid?[] ids = [mesh.Mesh, mesh.Source, mesh.Raster, mesh.Virtual?.Id, mesh.Bvh?.Id];
            meshInputs[index] = (id, path, ids.Where(value => value.HasValue)
                .Select(value => value!.Value.ToString("N") + ".siaasset").ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Content output requires an existing parent directory.");
        if (Path.Exists(output)) throw new IOException("Content output must not already exist.");
        var stage = Path.Combine(parent, ".content-cook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        void RequireStage() {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Content staging directory must not be a directory link.");
        }
        Exception? failure = null;
        try {
            var meshes = new List<ScenePack.Entry>();
            var layouts = new Dictionary<AssetId, MeshBakeLayout>();
            foreach (var input in meshInputs) {
                cancellationToken.ThrowIfCancellationRequested(); RequireStage();
                var bundle = Path.Combine(stage, input.Id.Value.ToString("N"));
                var cooked = MeshCook.Cook(input.Path, bundle, (ulong)maximumFileBytes, cancellationToken, mikkToolPath);
                foreach (var file in cooked.Files) input.Files.Add(file);
                if (cooked.Id != input.Id) throw new InvalidDataException("Mesh recipe identity changed during preparation.");
                if (cooked.BakeLayout is { } layout) layouts.Add(cooked.Id, layout);
                meshes.Add(new() { Id = cooked.Id.Value, Input = Path.Combine(bundle, cooked.Id.Value.ToString("N") + ".siaasset") });
            }
            var preparedRecipe = JsonSerializer.SerializeToUtf8Bytes(scene with { Meshes = meshes.ToArray() },
                ContentRecipeJsonContext.Default.SceneRecipe);
            if (preparedRecipe.Length > ScenePack.MaximumRecipeBytes) throw new InvalidDataException("Prepared scene recipe exceeds 1 MiB.");
            var sceneRecipePath = Path.Combine(stage, "scene.json");
            await using (var file = new FileStream(sceneRecipePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous)) await file.WriteAsync(preparedRecipe, cancellationToken);
            var published = await ScenePack.PackAsync(sceneRecipePath, output, maximumFileBytes, maximumTotalBytes, cancellationToken,
                recipe.Lightmap is { Environment: { } environment } hdr
                    ? hdr with { Environment = environment with { Input = Locate(environment.Input) } } : recipe.Lightmap, layouts);
            if (published != sceneId) throw new InvalidDataException("Unexpected published scene identity.");
            return published;
        }
        catch (Exception error) { failure = error; throw; }
        finally {
            try {
                RequireStage();
                var allowedDirectories = meshInputs.ToDictionary(input => Path.Combine(stage, input.Id.Value.ToString("N")),
                    input => input.Files, StringComparer.OrdinalIgnoreCase);
                foreach (var directory in Directory.GetDirectories(stage)) {
                    if (!allowedDirectories.TryGetValue(directory, out var files)
                        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || Directory.GetDirectories(directory).Length != 0)
                        throw new IOException("Unexpected content staging directory was preserved.");
                    foreach (var path in Directory.GetFiles(directory)) {
                        if (!files.Contains(Path.GetFileName(path)) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Content staging link was preserved.");
                        File.Delete(path);
                    }
                    Directory.Delete(directory, recursive: false);
                }
                foreach (var path in Directory.GetFiles(stage)) {
                    if (Path.GetFileName(path) != "scene.json" || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Unexpected content staging file was preserved.");
                    File.Delete(path);
                }
                Directory.Delete(stage, recursive: false);
            }
            catch (Exception cleanup) {
                if (failure is not null) throw new AggregateException("Content cooking failed and cleanup was incomplete.", failure, cleanup);
                throw new IOException("Content was published but staging cleanup was incomplete.", cleanup);
            }
        }
    }

    private static Recipe ReadRecipe(string path)
        => AssetInput.ReadJson(path, ContentRecipeJsonContext.Default.ContentRecipe, MaximumRecipeBytes);
}
#endif
