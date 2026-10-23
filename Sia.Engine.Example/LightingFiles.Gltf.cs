#if !BROWSER
using System.Text.Json;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class LightingFiles
{
    internal sealed record GltfLightIdentity
    {
        public required int Node { get; init; }
        public required Guid Id { get; init; }
        public bool CastShadows { get; init; }
    }

    internal sealed record GltfLightInput
    {
        public required string Input { get; init; }
        public required int Scene { get; init; }
        public required GltfLightIdentity[] Nodes { get; init; }
    }

    internal static async Task<PbrSceneLight[]> ReadGltfLightsAsync(GltfLightInput recipe, string directory,
        int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe); cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Input);
        if (recipe.Scene < 0 || recipe.Nodes is null || recipe.Nodes.Length > PbrSceneData.MaximumLights)
            throw new InvalidDataException("Invalid glTF lighting scene or identity table.");
        var path = Path.GetFullPath(recipe.Input, directory);
        if (!Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Source lighting requires a local JSON .gltf file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await AssetInput.ReadBoundedAsync(input, System.Math.Min(maximumBytes, 16 * 1024 * 1024), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 64, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0")
                throw new InvalidDataException("Source lighting requires glTF2.0.");
            var nodes = Array(root, "nodes", 65536); var scenes = Array(root, "scenes", 65536);
            if (recipe.Scene >= scenes.GetArrayLength()) throw new InvalidDataException("Selected glTF scene is absent.");
            var definitions = root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("KHR_lights_punctual", out var punctual)
                ? Array(punctual, "lights", PbrSceneData.MaximumLights) : Empty;
            var identities = new Dictionary<int, GltfLightIdentity>(); var ids = new HashSet<Guid>();
            foreach (var identity in recipe.Nodes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (identity is null || (uint)identity.Node >= (uint)nodes.GetArrayLength() || identity.Id == Guid.Empty
                    || !identities.TryAdd(identity.Node, identity) || !ids.Add(identity.Id))
                    throw new InvalidDataException("glTF light nodes require unique explicit nonempty identities.");
            }
            var scheduled = new bool[nodes.GetArrayLength()]; var work = new Stack<(int Node, float4x4 Parent)>();
            void Schedule(JsonElement children, float4x4 parent)
            {
                for (var at = children.GetArrayLength() - 1; at >= 0; at--) {
                    cancellationToken.ThrowIfCancellationRequested(); var node = children[at].GetInt32();
                    if ((uint)node >= (uint)scheduled.Length || scheduled[node])
                        throw new InvalidDataException("Selected glTF hierarchy has an invalid, cyclic or repeated node.");
                    scheduled[node] = true; work.Push((node, parent));
                }
            }
            Schedule(Array(scenes[recipe.Scene], "nodes", 65536), float4x4.identity);
            var lights = new List<PbrSceneLight>();
            while (work.TryPop(out var item)) {
                cancellationToken.ThrowIfCancellationRequested(); var node = nodes[item.Node];
                var world = math.mul(item.Parent, NodeMatrix(node));
                if (!math.all(math.isfinite(world.c0)) || !math.all(math.isfinite(world.c1))
                    || !math.all(math.isfinite(world.c2)) || !math.all(math.isfinite(world.c3)))
                    throw new InvalidDataException("glTF node-world transform exceeds finite Float32 admission.");
                if (node.TryGetProperty("extensions", out var nodeExtensions)) {
                    foreach (var extension in nodeExtensions.EnumerateObject()) {
                        if (extension.Name != "KHR_lights_punctual")
                            throw new NotSupportedException("Source lighting does not interpret this node extension: " + extension.Name);
                        var index = extension.Value.GetProperty("light").GetInt32();
                        if ((uint)index >= (uint)definitions.GetArrayLength() || !identities.Remove(item.Node, out var identity))
                            throw new InvalidDataException("Selected source light has no definition or authored identity.");
                        lights.Add(Light(definitions[index], world, identity));
                    }
                }
                Schedule(Array(node, "children", 65536), world);
            }
            if (identities.Count != 0) throw new InvalidDataException("A mapped light node is absent from the selected scene.");
            var values = lights.ToArray();
            _ = PbrSceneData.Create([], []).WithLights(values); // Domain admission is shared with the scene codec.
            return values;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) {
            throw new InvalidDataException("Invalid source glTF lighting metadata.", error);
        }
    }

    private static JsonElement Empty { get; } = EmptyArray();
    private static JsonElement EmptyArray()
    {
        using var document = JsonDocument.Parse("[]"); return document.RootElement.Clone();
    }
    private static JsonElement Array(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out var array)) return Empty;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidDataException("Invalid glTF lighting array: " + name);
        return array;
    }
    private static float Number(JsonElement parent, string name, float fallback)
        => parent.TryGetProperty(name, out var value) ? Finite(value) : fallback;
    private static float Finite(JsonElement value)
    {
        var number = value.GetSingle();
        return float.IsFinite(number) ? number : throw new InvalidDataException("Source light values must be finite Float32.");
    }
    private static float3 Vector(JsonElement parent, string name, float3 fallback)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) throw new InvalidDataException("Expected glTF XYZ triple.");
        return new(Finite(value[0]), Finite(value[1]), Finite(value[2]));
    }
    private static float4x4 NodeMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix)) {
            if (matrix.ValueKind != JsonValueKind.Array || matrix.GetArrayLength() != 16
                || node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _))
                throw new InvalidDataException("Source node requires a matrix or TRS, never both.");
            float4 Column(int at) => new(Finite(matrix[at]), Finite(matrix[at + 1]), Finite(matrix[at + 2]), Finite(matrix[at + 3]));
            var value = new float4x4(Column(0), Column(4), Column(8), Column(12));
            if (value.c0.w != 0 || value.c1.w != 0 || value.c2.w != 0 || value.c3.w != 1)
                throw new InvalidDataException("Source node matrix must be affine.");
            return value;
        }
        var rotation = quaternion.identity;
        if (node.TryGetProperty("rotation", out var q)) {
            if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() != 4) throw new InvalidDataException("Expected glTF quaternion.");
            var value = new float4(Finite(q[0]), Finite(q[1]), Finite(q[2]), Finite(q[3])); var squared = math.dot(value, value);
            if (!float.IsFinite(squared) || MathF.Abs(squared - 1) > 1e-5f) throw new InvalidDataException("Source quaternion must be normalized.");
            rotation = new(value);
        }
        return float4x4.TRS(Vector(node, "translation", default), rotation, Vector(node, "scale", new(1)));
    }
    private static PbrSceneLight Light(JsonElement light, float4x4 world, GltfLightIdentity identity)
    {
        if (light.TryGetProperty("extensions", out var extensions) && extensions.EnumerateObject().Any())
            throw new NotSupportedException("Source lighting does not interpret additional light extensions.");
        var kind = light.GetProperty("type").GetString() switch {
            "directional" => PbrSceneLightKind.Directional, "point" => PbrSceneLightKind.Point, "spot" => PbrSceneLightKind.Spot,
            _ => throw new NotSupportedException("Unsupported source punctual light type.")
        };
        var color = Vector(light, "color", new(1));
        if (math.any(color < 0) || math.any(color > 1)) throw new InvalidDataException("Source glTF light color must be within0..1.");
        var range = Number(light, "range", 0);
        if (light.TryGetProperty("range", out _) && (kind == PbrSceneLightKind.Directional || range <= 0))
            throw new InvalidDataException("Only local lights accept an explicit positive source range.");
        float inner = 0, outer = 0;
        if (light.TryGetProperty("spot", out var spot)) {
            if (kind != PbrSceneLightKind.Spot) throw new InvalidDataException("Only spot lights accept source cone settings.");
            if (spot.TryGetProperty("extensions", out var coneExtensions) && coneExtensions.EnumerateObject().Any())
                throw new NotSupportedException("Source lighting does not interpret additional cone extensions.");
            inner = Number(spot, "innerConeAngle", 0); outer = Number(spot, "outerConeAngle", MathF.PI / 4);
        }
        else if (kind == PbrSceneLightKind.Spot) outer = MathF.PI / 4;
        if (kind == PbrSceneLightKind.Spot && ((range > 0 && range <= .05f) || outer >= MathF.PI / 2))
            throw new NotSupportedException("Source spot range/cone exceeds the current renderer's admission.");
        var ray = kind == PbrSceneLightKind.Point ? default : -world.c2.xyz;
        if (kind != PbrSceneLightKind.Point) {
            var squared = math.lengthsq(ray);
            if (!float.IsFinite(squared) || squared < 1e-12f) throw new InvalidDataException("Source light direction is degenerate.");
            ray = math.normalize(ray);
        }
        return new(identity.Id, kind, color, Number(light, "intensity", 1), world.c3.xyz, ray, range, inner, outer, identity.CastShadows);
    }
}
#endif
