#if !BROWSER
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class ScenePack
{
    private static JsonElement EmptyCameraArray { get; } = CreateEmptyCameraArray();

    private static JsonElement CreateEmptyCameraArray()
    {
        using var document = JsonDocument.Parse("[]"); return document.RootElement.Clone();
    }

    internal sealed record CameraEntry
    {
        public required Guid Id { get; init; }
        public required float[] Position { get; init; }
        public required float[] Rotation { get; init; }
        public required float FovRadians { get; init; }
        public required float Near { get; init; }
        public required float Far { get; init; }
        [JsonConverter(typeof(JsonStringEnumConverter<PbrCameraFovAxis>))]
        public PbrCameraFovAxis FovAxis { get; init; }
    }

    internal sealed record GltfCameraIdentity
    {
        public required int Node { get; init; }
        public required Guid Id { get; init; }
    }

    internal sealed record GltfCameraInput
    {
        public required string Input { get; init; }
        public required int Scene { get; init; }
        public required GltfCameraIdentity[] Nodes { get; init; }
    }

    private static async Task<PbrSceneCamera[]?> ReadCamerasAsync(Recipe recipe, string directory,
        int maximumBytes, CancellationToken cancellationToken)
    {
        if (recipe.Cameras is not null && recipe.GltfCameras is not null)
            throw new InvalidDataException("Select typed cameras or glTF cameras, not both.");
        if (recipe.GltfCameras is { } gltf) return await ReadGltfCamerasAsync(gltf, directory, maximumBytes, cancellationToken);
        if (recipe.Cameras is not { } entries) return null;
        if (entries.Length > PbrSceneData.MaximumCameras) throw new InvalidDataException("Scene camera count limit exceeded.");
        var result = new PbrSceneCamera[entries.Length];
        for (var index = 0; index < result.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested(); var entry = entries[index];
            if (entry is null || entry.Position is not { Length: 3 } || entry.Rotation is not { Length: 4 })
                throw new InvalidDataException("Typed camera recipes require XYZ positions and XYZW quaternions.");
            result[index] = new(entry.Id, new(entry.Position[0], entry.Position[1], entry.Position[2]),
                new quaternion(entry.Rotation[0], entry.Rotation[1], entry.Rotation[2], entry.Rotation[3]),
                entry.FovRadians, entry.Near, entry.Far, entry.FovAxis);
        }
        ValidateCameraTable(result);
        return result;
    }

    private static void ValidateCameraTable(PbrSceneCamera[] cameras)
    {
        try { _ = PbrSceneData.Create([], []).WithCameras(cameras); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid authored camera metadata.", error); }
    }

    private static async Task<PbrSceneCamera[]> ReadGltfCamerasAsync(GltfCameraInput recipe, string directory,
        int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentException.ThrowIfNullOrWhiteSpace(recipe.Input);
        if (recipe.Scene < 0 || recipe.Nodes is null || recipe.Nodes.Length > PbrSceneData.MaximumCameras)
            throw new InvalidDataException("Invalid glTF camera scene or explicit identity table.");
        var path = Path.GetFullPath(recipe.Input, directory);
        if (!Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Source cameras require a local JSON .gltf file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await AssetInput.ReadBoundedAsync(input, System.Math.Min(maximumBytes, 16 * 1024 * 1024), cancellationToken);
        try {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 64, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0")
                throw new InvalidDataException("Source cameras require glTF2.0.");
            var nodes = CameraArray(root, "nodes", 65536); var scenes = CameraArray(root, "scenes", 65536);
            var definitions = CameraArray(root, "cameras", PbrSceneData.MaximumCameras);
            if (recipe.Scene >= scenes.GetArrayLength()) throw new InvalidDataException("Selected glTF scene is absent.");
            var identities = new Dictionary<int, Guid>(); var ids = new HashSet<Guid>();
            foreach (var identity in recipe.Nodes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (identity is null || (uint)identity.Node >= (uint)nodes.GetArrayLength() || identity.Id == Guid.Empty
                    || !identities.TryAdd(identity.Node, identity.Id) || !ids.Add(identity.Id))
                    throw new InvalidDataException("Camera nodes require unique explicit nonempty identities.");
            }
            var scheduled = new bool[nodes.GetArrayLength()]; var parents = new int[nodes.GetArrayLength()]; Array.Fill(parents, -1);
            var cameraPaths = new bool[nodes.GetArrayLength()];
            var work = new Stack<int>();
            void Schedule(JsonElement children, int parentNode)
            {
                for (var at = children.GetArrayLength() - 1; at >= 0; at--) {
                    cancellationToken.ThrowIfCancellationRequested(); var node = children[at].GetInt32();
                    if ((uint)node >= (uint)scheduled.Length || scheduled[node])
                        throw new InvalidDataException("Selected camera hierarchy has an invalid, cyclic or repeated node.");
                    scheduled[node] = true; parents[node] = parentNode; work.Push(node);
                }
            }
            Schedule(CameraArray(scenes[recipe.Scene], "nodes", 65536), -1);
            var selected = new List<(int Node, int Definition, Guid Id)>();
            while (work.TryPop(out var item)) {
                cancellationToken.ThrowIfCancellationRequested(); var node = nodes[item];
                if (node.TryGetProperty("camera", out var camera)) {
                    var definition = camera.GetInt32();
                    if ((uint)definition >= (uint)definitions.GetArrayLength() || !identities.Remove(item, out var id))
                        throw new InvalidDataException("Every selected camera node requires a definition and explicit identity.");
                    for (var parent = item; parent >= 0 && !cameraPaths[parent]; parent = parents[parent]) {
                        cancellationToken.ThrowIfCancellationRequested(); cameraPaths[parent] = true;
                    }
                    selected.Add((item, definition, id));
                    if (selected.Count > PbrSceneData.MaximumCameras) throw new InvalidDataException("Scene camera count limit exceeded.");
                }
                Schedule(CameraArray(node, "children", 65536), item);
            }
            if (identities.Count != 0) throw new InvalidDataException("A mapped camera node is absent from the selected scene.");
            foreach (var animation in CameraArray(root, "animations", 65536).EnumerateArray())
                foreach (var channel in CameraArray(animation, "channels", 65536).EnumerateArray()) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = channel.GetProperty("target");
                    if (target.TryGetProperty("extensions", out var targetExtensions) && targetExtensions.EnumerateObject().Any())
                        throw new NotSupportedException("Source cameras do not interpret animation-target extensions.");
                    if (target.TryGetProperty("node", out var node) && (uint)node.GetInt32() < (uint)cameraPaths.Length && cameraPaths[node.GetInt32()])
                        throw new NotSupportedException("Authored cameras snapshot static paths; animated camera nodes or ancestors require explicit offline evaluation.");
                }
            for (var index = 0; index < cameraPaths.Length; index++) if (cameraPaths[index]
                && nodes[index].TryGetProperty("extensions", out var extensions)
                && extensions.EnumerateObject().Any(value => value.Name != "KHR_lights_punctual"))
                throw new NotSupportedException("Source cameras do not interpret additional camera-path node extensions.");
            // Evaluate only camera ancestor paths. Unrelated Mesh scales,
            // matrix decompositions and extension semantics are not camera input.
            var worlds = new Dictionary<int, float4x4>(); var pathNodes = new Stack<int>();
            var result = new PbrSceneCamera[selected.Count];
            for (var index = 0; index < selected.Count; index++) {
                var item = selected[index]; var ancestor = item.Node;
                while (ancestor >= 0 && !worlds.ContainsKey(ancestor)) {
                    cancellationToken.ThrowIfCancellationRequested(); pathNodes.Push(ancestor); ancestor = parents[ancestor];
                }
                var world = ancestor < 0 ? float4x4.identity : worlds[ancestor];
                while (pathNodes.TryPop(out var current)) {
                    cancellationToken.ThrowIfCancellationRequested(); world = math.mul(world, CameraNodeMatrix(nodes[current]));
                    if (!math.all(math.isfinite(world.c0)) || !math.all(math.isfinite(world.c1))
                        || !math.all(math.isfinite(world.c2)) || !math.all(math.isfinite(world.c3)))
                        throw new InvalidDataException("Source camera path exceeds finite Float32 world admission.");
                    worlds.Add(current, world);
                }
                result[index] = ReadCameraDefinition(definitions[item.Definition], world, item.Id);
            }
            ValidateCameraTable(result); return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) {
            throw new InvalidDataException("Invalid source glTF camera metadata.", error);
        }
    }

    private static PbrSceneCamera ReadCameraDefinition(JsonElement definition, float4x4 world, Guid id)
    {
        if (definition.GetProperty("type").GetString() != "perspective") throw new NotSupportedException("Only perspective source cameras are supported.");
        if (definition.TryGetProperty("extensions", out var extensions) && extensions.EnumerateObject().Any())
            throw new NotSupportedException("Source cameras do not interpret projection extensions.");
        var perspective = definition.GetProperty("perspective");
        if (perspective.TryGetProperty("aspectRatio", out _))
            throw new NotSupportedException("Fixed-aspect cameras require an explicit viewport policy.");
        if (perspective.TryGetProperty("extensions", out var projectionExtensions) && projectionExtensions.EnumerateObject().Any())
            throw new NotSupportedException("Source cameras do not interpret perspective extensions.");
        if (!perspective.TryGetProperty("zfar", out var far)) throw new NotSupportedException("Infinite-far cameras require an explicit projection policy.");
        var x = world.c0.xyz; var y = world.c1.xyz; var z = world.c2.xyz;
        var determinant = math.dot(math.cross(x, y), z);
        // Accept only bounded Float32 export noise around a rigid basis. Real
        // scale/shear/mirroring is unsupported rather than silently discarded.
        if (world.c0.w != 0 || world.c1.w != 0 || world.c2.w != 0 || world.c3.w != 1
            || MathF.Abs(math.lengthsq(x) - 1) > 1e-5f || MathF.Abs(math.lengthsq(y) - 1) > 1e-5f
            || MathF.Abs(math.lengthsq(z) - 1) > 1e-5f || MathF.Abs(math.dot(x, y)) > 1e-5f
            || MathF.Abs(math.dot(x, z)) > 1e-5f || MathF.Abs(math.dot(y, z)) > 1e-5f
            || !float.IsFinite(determinant) || determinant <= 0 || MathF.Abs(determinant - 1) > 2e-5f)
            throw new NotSupportedException("Source camera world transform must be rigid within the fixed Float32 export envelope; scale, shear and mirroring are unsupported.");
        // This explicit producer conversion projects accepted near-rigid export
        // noise to a unit quaternion. The stored domain/codec never normalizes.
        var rotation = math.normalize(new quaternion(new float3x3(x, y, z)));
        return new(id, world.c3.xyz, rotation, CameraFinite(perspective.GetProperty("yfov")),
            CameraFinite(perspective.GetProperty("znear")), CameraFinite(far));
    }

    private static JsonElement CameraArray(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out var array)) return EmptyCameraArray;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidDataException("Invalid source camera array: " + name);
        return array;
    }
    private static float CameraFinite(JsonElement value)
    {
        var number = value.GetSingle(); return float.IsFinite(number) ? number : throw new InvalidDataException("Camera source values must be finite Float32.");
    }
    private static float3 CameraVector(JsonElement parent, string name, float3 fallback)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) throw new InvalidDataException("Expected source camera XYZ triple.");
        return new(CameraFinite(value[0]), CameraFinite(value[1]), CameraFinite(value[2]));
    }
    private static float4x4 CameraNodeMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix)) {
            if (matrix.ValueKind != JsonValueKind.Array || matrix.GetArrayLength() != 16
                || node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _))
                throw new InvalidDataException("Source camera nodes require a matrix or TRS, never both.");
            float4 Column(int at) => new(CameraFinite(matrix[at]), CameraFinite(matrix[at + 1]), CameraFinite(matrix[at + 2]), CameraFinite(matrix[at + 3]));
            var result = new float4x4(Column(0), Column(4), Column(8), Column(12));
            if (result.c0.w != 0 || result.c1.w != 0 || result.c2.w != 0 || result.c3.w != 1) throw new InvalidDataException("Source camera node matrices must be affine.");
            return result;
        }
        var rotation = quaternion.identity;
        if (node.TryGetProperty("rotation", out var q)) {
            if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() != 4) throw new InvalidDataException("Expected source camera XYZW quaternion.");
            var value = new float4(CameraFinite(q[0]), CameraFinite(q[1]), CameraFinite(q[2]), CameraFinite(q[3]));
            if (!float.IsFinite(math.lengthsq(value)) || MathF.Abs(math.lengthsq(value) - 1) > 1e-5f)
                throw new InvalidDataException("Source camera node quaternions must be near unit.");
            rotation = new(value);
        }
        return float4x4.TRS(CameraVector(node, "translation", default), rotation, CameraVector(node, "scale", new(1)));
    }
}
#endif
