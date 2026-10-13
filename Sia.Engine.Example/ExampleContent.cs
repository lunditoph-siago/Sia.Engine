using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sia.Engine.Example;

// The example's four entry points, not an asset registry or asset-core policy.
internal static partial class ExampleContent
{
    internal sealed record Entry
    {
        public required string Scene { get; init; }
        public string? Lightmaps { get; init; }
    }

    internal sealed record Catalog
    {
        public required Entry Bunny { get; init; }
        public required Entry BistroExterior { get; init; }
        public required Entry BistroInterior { get; init; }
        public required Entry BistroWine { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Catalog))]
    private partial class CatalogJsonContext : JsonSerializerContext;

    internal static Catalog Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 4096) throw new InvalidDataException("Example content catalog exceeds 4 KiB.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Example content catalog changed while reading.");
        return JsonSerializer.Deserialize(bytes, CatalogJsonContext.Default.Catalog)
            ?? throw new InvalidDataException("Example content catalog must be an object.");
    }

    internal static void Validate(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        static void Path(string? path, string suffix) {
            var parts = path?.Split('/');
            if (parts is not { Length: 3 } || parts[0] != "Content" || parts[1].Length != 64
                || parts[1].Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !parts[2].EndsWith(suffix, StringComparison.Ordinal)
                || !Guid.TryParseExact(parts[2][..^suffix.Length], "N", out var id) || id == Guid.Empty)
                throw new InvalidDataException("Example content paths require Content/<sha256>/<nonempty-uuid> and the selected record suffix.");
        }
        foreach (var entry in new[] { catalog.Bunny, catalog.BistroExterior, catalog.BistroInterior, catalog.BistroWine }) {
            if (entry is null) throw new InvalidDataException("Every default requires a scene entry.");
            Path(entry.Scene, ".siascene");
            if (entry.Lightmaps is { } lightmaps) {
                Path(lightmaps, ".siaasset");
                if (lightmaps[..lightmaps.LastIndexOf('/')] != entry.Scene[..entry.Scene.LastIndexOf('/')])
                    throw new InvalidDataException("Default scene and lighting must belong to the same checked content snapshot.");
            }
        }
    }

#if !BROWSER
    internal static Entry Default(ScenePipeline pipeline, string assetsDirectory)
    {
        var catalog = Read(Path.Combine(assetsDirectory, "default-scenes.json")); Validate(catalog);
        var entry = pipeline == ScenePipeline.Bunny ? catalog.Bunny : catalog.BistroExterior;
        string Resolve(string relative) => Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        return new() { Scene = Resolve(entry.Scene), Lightmaps = entry.Lightmaps is { } lightmaps ? Resolve(lightmaps) : null };
    }
#endif
}
