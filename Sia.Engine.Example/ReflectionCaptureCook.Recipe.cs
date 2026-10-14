#if !BROWSER
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class ReflectionCaptureCook
{
    internal sealed record Recipe
    {
        public required string Scene { get; init; }
        public required string SourceEnvironment { get; init; }
        public required Guid Product { get; init; }
        public required Guid Environment { get; init; }
        public required Guid[] StaticInstances { get; init; }
        public required float[] Position { get; init; }
        public required float[] BoundsMin { get; init; }
        public required float[] BoundsMax { get; init; }
        public float[] TowardLight { get; init; } = [0, 1, 0];
        public float[] LightRadiance { get; init; } = [0, 0, 0];
        public int FilterSamples { get; init; } = 64;
        public float MaximumDistance { get; init; } = 1000;
        public float RayBias { get; init; } = .002f;
        public ulong MaximumWorkingBytes { get; init; } = 32ul * 1024 * 1024;
        public ulong MaximumStagingBytes { get; init; } = 4ul * 1024 * 1024;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 8)]
    [JsonSerializable(typeof(Recipe))]
    private partial class CaptureRecipeJsonContext : JsonSerializerContext;

    internal static async Task<Result> CookAsync(string recipePath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); recipePath = Path.GetFullPath(recipePath);
        ReadOnlyMemory<byte> encoded;
        using (var input = File.OpenRead(recipePath))
            encoded = await AssetInput.ReadBoundedAsync(input, 1024 * 1024, cancellationToken);
        var recipe = JsonSerializer.Deserialize(encoded.Span, CaptureRecipeJsonContext.Default.Recipe)
            ?? throw new InvalidDataException("Missing capture recipe.");
        static float3 Vector(float[] values) => values is { Length: 3 } && values.All(float.IsFinite)
            ? new(values[0], values[1], values[2]) : throw new InvalidDataException("Capture vectors require three finite components.");
        var position = Vector(recipe.Position); var bounds = new Aabb(Vector(recipe.BoundsMin), Vector(recipe.BoundsMax));
        var settings = new PbrReflectionCaptureSettings { TowardLight = Vector(recipe.TowardLight), LightRadiance = Vector(recipe.LightRadiance),
            FilterSamples = recipe.FilterSamples, MaximumDistance = recipe.MaximumDistance, RayBias = recipe.RayBias };
        string Location(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return AssetInput.TryGetHttpUri(value, out _) ? value : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(recipePath)!, value));
        }
        var scenePath = Location(recipe.Scene); var sourcePath = Location(recipe.SourceEnvironment);
        var sceneFile = AssetInput.TryGetHttpUri(scenePath, out var sceneUri) ? sceneUri!.AbsolutePath : scenePath;
        if (!sceneFile.EndsWith(".siascene", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(sceneFile), "N", out var sceneId) || sceneId == Guid.Empty)
            throw new InvalidDataException("Capture scene requires a UUID .siascene path.");
        var sourceFile = AssetInput.TryGetHttpUri(sourcePath, out var uri) ? uri!.AbsolutePath : sourcePath;
        if (!sourceFile.EndsWith(".siaasset", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(sourceFile), "N", out var sourceId) || sourceId == Guid.Empty)
            throw new InvalidDataException("Capture source environment requires a UUID .siaasset path.");
        // Pure validation before dependency IO; the real scene/content hashes replace these placeholders after preparation.
        _ = PbrReflectionCaptureData.Create(new(recipe.Product), new(new AssetId(sceneId)), new string('0', 64),
            new(new(sourceId), new string('0', 64)), new(new(recipe.Environment), new string('0', 64)),
            position, bounds, settings, recipe.StaticInstances);
        using var http = new HttpClient();
        var scene = await PbrSceneFiles.PrepareBvhAsync(scenePath, http, cancellationToken: cancellationToken);
        byte[] sourceBytes;
        using (var input = await AssetInput.OpenAsync(sourcePath, http, cancellationToken))
            sourceBytes = await AssetInput.ReadExactAsync(input, IblEnvironmentAsset.EncodedBytes, cancellationToken);
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        var source = IblEnvironmentRecord.Load(sourceStream, sourcePath); cancellationToken.ThrowIfCancellationRequested();
        return Cook(scene, source, new(sourceId), new(recipe.Environment), new(recipe.Product), recipe.StaticInstances,
            position, bounds, outputDirectory, settings, recipe.MaximumWorkingBytes, recipe.MaximumStagingBytes, cancellationToken);
    }
}
#endif
