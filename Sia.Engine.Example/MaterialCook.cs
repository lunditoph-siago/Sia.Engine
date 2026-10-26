#if !BROWSER
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Example;

// Explicit authoring inputs; source-specific texture-role policy stays outside the renderer.
internal static partial class MaterialCook
{
    internal sealed record Recipe
    {
        public required Guid Material { get; init; }
        public float[] BaseColor { get; init; } = [.8f, .8f, .8f];
        public float Metallic { get; init; }
        public float Roughness { get; init; } = .5f;
        public float[] EmissiveColor { get; init; } = [0, 0, 0];
        public float EmissiveStrength { get; init; }
        public Guid? BaseColorTexture { get; init; }
        public Guid? NormalTexture { get; init; }
        public Guid? MetallicRoughnessTexture { get; init; }
        public Guid? OcclusionTexture { get; init; }
        public Guid? EmissiveTexture { get; init; }
        public float NormalScale { get; init; } = 1;
        public float OcclusionStrength { get; init; } = 1;
        public bool DoubleSided { get; init; }
        public bool AlphaBlend { get; init; }
        public float Opacity { get; init; } = 1;
        public float Transmission { get; init; }
        public float Thickness { get; init; }
        public DiffuseSources? DiffuseSources { get; init; }

        internal PbrMaterialData Data(PbrDiffuseMoments? diffuseMoments = null)
        {
            if (Material == Guid.Empty || BaseColor is not { Length: 3 } || EmissiveColor is not { Length: 3 })
                throw new InvalidDataException("Material cook requires an authored identity and two RGB triples.");
            TextureReference? Reference(Guid? id) => id is null ? null : id == Guid.Empty
                ? throw new InvalidDataException("A present texture reference must have a nonempty authored identity.")
                : new(new(id.Value));
            return new(new(new float3(BaseColor[0], BaseColor[1], BaseColor[2]), Metallic, Roughness,
                new(EmissiveColor[0], EmissiveColor[1], EmissiveColor[2]), EmissiveStrength),
                Reference(BaseColorTexture), Reference(NormalTexture), Reference(MetallicRoughnessTexture),
                Reference(OcclusionTexture), Reference(EmissiveTexture), NormalScale, OcclusionStrength,
                DoubleSided, AlphaBlend, Opacity, Transmission, Thickness, diffuseMoments);
        }
    }

    internal sealed record DiffuseSources
    {
        public string? BaseColor { get; init; }
        public string? MetallicRoughness { get; init; }
        public ulong MaximumBytes { get; init; } = 256ul * 1024 * 1024;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4)]
    [JsonSerializable(typeof(Recipe))]
    private partial class MaterialRecipeJsonContext : JsonSerializerContext;

    internal static AssetId Cook(string recipePath, string outputDirectory,
        ulong maximumBytes = PbrMaterialData.MaximumEncodedBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recipePath = Path.GetFullPath(recipePath);
        var recipe = AssetInput.ReadJson(recipePath, MaterialRecipeJsonContext.Default.Recipe, 8192);
        var data = recipe.Data();
        if (maximumBytes < (ulong)(recipe.DiffuseSources is null ? data.EncodedSize : PbrMaterialData.MaximumEncodedBytes))
            throw new InvalidDataException("Material metadata exceeds its encoded publication budget.");
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Material output requires an existing parent directory.");
        if (Path.Exists(output)) throw new IOException("Material output must not already exist.");
        if (recipe.DiffuseSources is { } sources) {
            if ((sources.BaseColor is not null) != (recipe.BaseColorTexture is not null)
                || (sources.MetallicRoughness is not null) != (recipe.MetallicRoughnessTexture is not null))
                throw new InvalidDataException("Diffuse source paths must match the authored texture roles.");
            FileStream? Open(string? path) => path is null ? null
                : new FileStream(Path.GetFullPath(path, Path.GetDirectoryName(recipePath)!), FileMode.Open,
                    FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            using var color = Open(sources.BaseColor);
            using var metal = Open(sources.MetallicRoughness);
            data = recipe.Data(PbrDiffuseMoments.Compute(color, metal, sources.MaximumBytes, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var stage = Path.Combine(parent, ".material-cook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var file = Path.Combine(stage, recipe.Material.ToString("N") + ".siaasset");
        var files = new List<string>();
        try {
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                files.Add(file);
                data.Save(stream);
            }
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return new(recipe.Material);
        }
        catch (Exception error) {
            try {
                OfflineDirectory.RequireFiles(stage, files, CancellationToken.None);
                foreach (var owned in files) File.Delete(owned);
                Directory.Delete(stage, recursive: false);
            }
            catch (Exception cleanup) { throw new AggregateException("Material cook failed and staging cleanup was incomplete.", error, cleanup); }
            throw;
        }
    }
}
#endif
