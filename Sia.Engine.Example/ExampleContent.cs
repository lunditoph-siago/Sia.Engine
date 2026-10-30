using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 4, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Catalog))]
    private partial class CatalogJsonContext : JsonSerializerContext;

#if !BROWSER
    internal static Catalog Read(string path)
    {
        return AssetInput.ReadJson(path, CatalogJsonContext.Default.Catalog, 16384);
    }
#endif

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

    internal static async Task<Entry> DefaultAsync(string assetsDirectory, HttpClient http,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory); ArgumentNullException.ThrowIfNull(http);
        cancellationToken.ThrowIfCancellationRequested();
        var remote = AssetInput.TryGetHttpUri(assetsDirectory, out var uri);
#if BROWSER
        if (!remote) throw new ArgumentException("Browser content requires an HTTP package directory.", nameof(assetsDirectory));
#endif
        string Resolve(string relative) {
            if (!remote) return Path.Combine(assetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var root = new UriBuilder(uri!) { Path = uri!.AbsolutePath.TrimEnd('/') + "/" };
            return new UriBuilder(new Uri(root.Uri, relative)) { Query = uri.Query.TrimStart('?') }.Uri.AbsoluteUri;
        }
        using var input = await AssetInput.OpenAsync(Resolve("default-scenes.json"), http, cancellationToken);
        var bytes = await AssetInput.ReadBoundedAsync(input, 16384, cancellationToken);
        Catalog Decode(CancellationToken token) {
            token.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize(bytes.Span, CatalogJsonContext.Default.Catalog)
                ?? throw new InvalidDataException("Example content catalog must be an object.");
        }
#if BROWSER
        var catalog = await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
        var catalog = Decode(cancellationToken);
#endif
        Validate(catalog);
#if BROWSER
        if (catalog.Bunny.ReflectionCaptures.Length != 0)
            throw new NotSupportedException("Static reflection captures are not supported in browser packages.");
#endif
        return MapPaths(catalog.Bunny, Resolve);
    }
}
