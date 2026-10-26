#if !BROWSER
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class HdrEnvironmentCook
{
    internal sealed record Recipe
    {
        public required Guid Environment { get; init; }
        public required string Input { get; init; }
        public required string BrdfSource { get; init; }
        public required float Intensity { get; init; }
        public float[] SourceToWorld { get; init; } = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        public int FilterSamples { get; init; } = 256;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4)]
    [JsonSerializable(typeof(Recipe))]
    private partial class HdrRecipeJsonContext : JsonSerializerContext;

    internal static AssetId Cook(string recipePath, string outputDirectory,
        ulong maximumInputBytes = 128ul * 1024 * 1024, ulong maximumWorkingBytes = 64ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recipePath = Path.GetFullPath(recipePath);
        var recipe = AssetInput.ReadJson(recipePath, HdrRecipeJsonContext.Default.Recipe, 8192);
        if (recipe.Environment == Guid.Empty || string.IsNullOrWhiteSpace(recipe.Input) || string.IsNullOrWhiteSpace(recipe.BrdfSource)
            || recipe.SourceToWorld is not { Length: 9 })
            throw new InvalidDataException("HDR cook requires an authored environment identity, two input paths and a column-major 3x3 basis.");
        // Reserve template decode, serializer payload, file buffers and recipe alongside producer admission.
        const ulong fixedBytes = 4ul * IblEnvironmentAsset.EncodedBytes + 128 * 1024;
        if (maximumWorkingBytes <= fixedBytes) throw new ArgumentException("HDR cook cannot fit its fixed working admission.", nameof(maximumWorkingBytes));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("HDR output requires an existing parent directory.");
        if (Path.Exists(output)) throw new IOException("HDR output must not already exist.");
        var directory = Path.GetDirectoryName(recipePath)!;
        var template = FileLoader.Load<IblEnvironmentRecord>(Path.GetFullPath(recipe.BrdfSource, directory));
        var m = recipe.SourceToWorld;
        IblEnvironmentAsset environment;
        using (var input = new FileStream(Path.GetFullPath(recipe.Input, directory), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan)) {
            environment = IblEnvironmentBaker.BakeHdr(input, template.Data, recipe.Intensity,
                new float3x3(new(m[0], m[1], m[2]), new(m[3], m[4], m[5]), new(m[6], m[7], m[8])),
                recipe.FilterSamples, maximumInputBytes, maximumWorkingBytes - fixedBytes, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var stage = Path.Combine(parent, ".hdr-cook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var files = new List<string>();
        try {
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            var path = Path.Combine(stage, recipe.Environment.ToString("N") + ".siaasset");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                files.Add(path); environment.Write(file);
            }
            OfflineDirectory.RequireFiles(stage, files, cancellationToken);
            OfflineDirectory.Publish(stage, output, cancellationToken);
            return new(recipe.Environment);
        }
        catch (Exception error) {
            try {
                OfflineDirectory.RequireFiles(stage, files, CancellationToken.None);
                foreach (var file in files) File.Delete(file);
                Directory.Delete(stage, recursive: false);
            }
            catch (Exception cleanup) { throw new AggregateException("HDR cook failed and staging cleanup was incomplete.", error, cleanup); }
            throw;
        }
    }
}
#endif
