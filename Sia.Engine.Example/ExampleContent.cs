using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

// The example's four entry points, not an asset registry or asset-core policy.
internal static partial class ExampleContent
{
    internal sealed record Entry
    {
        public required string Scene { get; init; }
        public string? Lightmaps { get; init; }
        public string? Environment { get; init; }
    }

    internal sealed record Catalog
    {
        public required Entry Bunny { get; init; }
        public Entry? BistroExterior { get; init; }
        public Entry? BistroInterior { get; init; }
        public Entry? BistroWine { get; init; }
    }

#if !BROWSER
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Catalog))]
    private partial class CatalogJsonContext : JsonSerializerContext;

    internal static Catalog Read(string path)
    {
        return AssetInput.ReadJson(path, CatalogJsonContext.Default.Catalog, 16384);
    }

    internal static void Validate(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        static void Path(string? path, string suffix) {
            var parts = path?.Split('/');
            if (parts is not { Length: 3 } || parts[0] != "Content" || parts[1].Length != 64
                || parts[1].Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !parts[2].EndsWith(suffix, StringComparison.Ordinal)
                || !Guid.TryParseExact(parts[2][..^suffix.Length], "N", out var id) || id == Guid.Empty
                || parts[2][..^suffix.Length] != id.ToString("N"))
                throw new InvalidDataException("Example content paths require Content/<sha256>/<nonempty-uuid> and the selected record suffix.");
        }
        var bistro = new[] { catalog.BistroExterior, catalog.BistroInterior, catalog.BistroWine };
        if (bistro.Any(entry => entry is not null) && bistro.Any(entry => entry is null))
            throw new InvalidDataException("Native content requires all three Bistro scenes.");
        foreach (var entry in new[] { catalog.Bunny }.Concat(bistro.OfType<Entry>())) {
            if (entry is null) throw new InvalidDataException("Every default requires a scene entry.");
            Path(entry.Scene, ".siascene");
            foreach (var lighting in new[] { entry.Lightmaps, entry.Environment }.OfType<string>()) {
                Path(lighting, ".siaasset");
                if (lighting[..lighting.LastIndexOf('/')] != entry.Scene[..entry.Scene.LastIndexOf('/')])
                    throw new InvalidDataException("Default scene and lighting must belong to the same checked content snapshot.");
            }
        }
    }

    internal static Entry MapPaths(Entry entry, Func<string, string> map)
        => entry with { Scene = map(entry.Scene), Lightmaps = entry.Lightmaps is { } lightmaps ? map(lightmaps) : null,
            Environment = entry.Environment is { } environment ? map(environment) : null };

    internal static Entry Default(ScenePipeline pipeline, string assetsDirectory, RenderQuality quality = RenderQuality.High)
    {
        var catalog = Read(Path.Combine(assetsDirectory, "default-scenes.json")); Validate(catalog);
        var entry = pipeline == ScenePipeline.Bunny ? catalog.Bunny : catalog.BistroExterior
            ?? throw new InvalidDataException("Bistro requires a native content package.");
        string Resolve(string relative) => Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (pipeline == ScenePipeline.Bunny && quality == RenderQuality.Low) entry = entry with { Lightmaps = null };
        return MapPaths(entry, Resolve);
    }
#endif
}
