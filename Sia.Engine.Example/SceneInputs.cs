using System.Text.Json;
using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

// A description owns no assets. Existing readers and renderer owners validate and own them.
internal readonly record struct SceneInputs(string Resident, string Stream, string Environment,
    string Lightmaps, string Probes, string? Reflections = null)
{
    internal const int MaximumBytes = 64 * 1024;

    internal static bool IsDescription(string? path)
        => path?.Split('?', '#')[0].EndsWith(".siascene", StringComparison.OrdinalIgnoreCase) == true;

    internal string SelectScene(RenderQuality quality, bool? lodOverride = null)
    {
        if (quality is not (RenderQuality.Low or RenderQuality.Medium or RenderQuality.High))
            throw new ArgumentOutOfRangeException(nameof(quality));
        return quality == RenderQuality.Low || lodOverride.HasValue ? Resident : Stream;
    }

    internal static Task<SceneInputs> LoadAsync(string path, HttpClient http, CancellationToken cancellationToken = default)
        => AssetInput.LoadBoundedAsync(path, http, MaximumBytes, bytes => Decode(bytes).Resolve(path), cancellationToken);

    internal SceneInputs Resolve(string description)
    {
        Validate();
        return new(ResolvePath(Resident), ResolvePath(Stream), ResolvePath(Environment), ResolvePath(Lightmaps),
            ResolvePath(Probes), Reflections is null ? null : ResolvePath(Reflections));

        string ResolvePath(string reference) => AssetInput.TryGetHttpUri(description, out var uri)
            ? new Uri(uri!, reference).AbsoluteUri
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(description))!, reference));
    }

    internal byte[] Encode()
    {
        Validate();
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 1);
            writer.WriteString(nameof(Resident), Resident);
            writer.WriteString(nameof(Stream), Stream);
            writer.WriteString(nameof(Environment), Environment);
            writer.WriteString(nameof(Lightmaps), Lightmaps);
            writer.WriteString(nameof(Probes), Probes);
            if (Reflections is not null) writer.WriteString(nameof(Reflections), Reflections);
            writer.WriteEndObject();
        }
        if (output.Length > MaximumBytes) throw new InvalidDataException("Scene description exceeds its size limit.");
        return output.ToArray();
    }

    internal static SceneInputs Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Scene description exceeds its size limit.");
        try {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Scene description must be an object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject()) {
                if (field.Name is not ("Version" or nameof(Resident) or nameof(Stream) or nameof(Environment)
                    or nameof(Lightmaps) or nameof(Probes) or nameof(Reflections)) || !fields.TryAdd(field.Name, field.Value))
                    throw new InvalidDataException("Unknown or duplicate scene description field: " + field.Name);
            }
            if (!fields.TryGetValue("Version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var value) || value != 1)
                throw new InvalidDataException("Unsupported scene description version.");
            var result = new SceneInputs(Required(nameof(Resident)), Required(nameof(Stream)), Required(nameof(Environment)),
                Required(nameof(Lightmaps)), Required(nameof(Probes)),
                fields.ContainsKey(nameof(Reflections)) ? Required(nameof(Reflections)) : null);
            result.Validate();
            return result;

            string Required(string name) => fields.TryGetValue(name, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString()! : throw new InvalidDataException("Missing or invalid scene reference: " + name);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid scene description JSON.", error); }
    }

    private void Validate()
    {
        Reference(Resident, ".siapbr"); Reference(Stream, ".siastream"); Reference(Environment, ".siaenv");
        Reference(Lightmaps, ".sialmst", ".sialmap"); Reference(Probes, ".siaprobe");
        if (Reflections is not null) Reference(Reflections, ".siarefl");

        static void Reference(string reference, params string[] extensions)
        {
            if (string.IsNullOrEmpty(reference) || reference.Length > 2048
                || reference.Any(c => char.IsControl(c) || "\\:%?#\"<>|*".Contains(c))
                || reference.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part != part.Trim())
                || !extensions.Any(extension => reference.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Scene references must be relative child paths with the expected asset extension.");
        }
    }
}
