using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

internal static partial class ExampleContent
{
    internal sealed record Entry
    {
        public required string Scene { get; init; }
        public string? Lightmaps { get; init; }
        public string? Environment { get; init; }
        public string[] ReflectionCaptures { get; init; } = [];
    }

    internal sealed record Catalog
    {
        public required Entry Bunny { get; init; }
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
        var entry = catalog.Bunny ?? throw new InvalidDataException("Bunny requires a scene entry.");
        Path(entry.Scene, ".siascene");
        if (entry.ReflectionCaptures is null || entry.ReflectionCaptures.Length > 16
            || entry.ReflectionCaptures.Distinct(StringComparer.Ordinal).Count() != entry.ReflectionCaptures.Length)
            throw new InvalidDataException("Default captures require at most sixteen distinct references.");
        foreach (var lighting in entry.ReflectionCaptures.Concat(new[] { entry.Lightmaps, entry.Environment }.OfType<string>())) {
            Path(lighting, ".siaasset");
            if (lighting[..lighting.LastIndexOf('/')] != entry.Scene[..entry.Scene.LastIndexOf('/')])
                throw new InvalidDataException("Default scene and lighting must belong to the same checked content snapshot.");
        }
    }

    internal static Entry MapPaths(Entry entry, Func<string, string> map)
        => entry with { Scene = map(entry.Scene), Lightmaps = entry.Lightmaps is { } lightmaps ? map(lightmaps) : null,
            Environment = entry.Environment is { } environment ? map(environment) : null,
            ReflectionCaptures = entry.ReflectionCaptures.Select(map).ToArray() };

    internal static Entry Default(string assetsDirectory, RenderQuality quality = RenderQuality.High)
    {
        var catalog = Read(Path.Combine(assetsDirectory, "default-scenes.json")); Validate(catalog);
        var entry = catalog.Bunny;
        string Resolve(string relative) => Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (quality == RenderQuality.Low) entry = entry with { Lightmaps = null };
        return MapPaths(entry, Resolve);
    }
#endif
}
