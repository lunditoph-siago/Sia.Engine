using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sia.Engine.Example;

// The example's four entry points, not an asset registry or asset-core policy.
internal static partial class ExampleContent
{
    internal sealed record Catalog
    {
        public required string Bunny { get; init; }
        public required string BistroExterior { get; init; }
        public required string BistroInterior { get; init; }
        public required string BistroWine { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4)]
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
        foreach (var path in new[] { catalog.Bunny, catalog.BistroExterior, catalog.BistroInterior, catalog.BistroWine }) {
            var parts = path?.Split('/');
            if (parts is not { Length: 3 } || parts[0] != "Content" || parts[1].Length != 64
                || parts[1].Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !parts[2].EndsWith(".siascene", StringComparison.Ordinal)
                || !Guid.TryParseExact(parts[2][..^9], "N", out var id) || id == Guid.Empty
                || parts[2][..^9] != id.ToString("N"))
                throw new InvalidDataException("Example scene paths require Content/<sha256>/<uuid>.siascene.");
        }
    }

#if !BROWSER
    internal static string DefaultScene(ScenePipeline pipeline, string assetsDirectory)
    {
        var catalog = Read(Path.Combine(assetsDirectory, "default-scenes.json")); Validate(catalog);
        var relative = pipeline == ScenePipeline.Bunny ? catalog.Bunny : catalog.BistroExterior;
        return Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }
#endif
}
