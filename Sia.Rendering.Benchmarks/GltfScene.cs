using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class GltfScene
{
    private readonly JsonElement _root;
    private readonly string _directory;
    private readonly byte[][] _buffers;
    private readonly int _textureSize;
    private readonly CancellationToken _cancellation;

    private GltfScene(JsonElement root, string directory, byte[]? binary, int textureSize, CancellationToken cancellation)
    {
        _root = root; _directory = directory; _textureSize = textureSize; _cancellation = cancellation;
        Require(root.GetProperty("asset").GetProperty("version").GetString() == "2.0", "Only glTF 2.0 is supported.");
        Reject(root, "extensionsRequired", "animations", "skins");
        _buffers = root.GetProperty("buffers").EnumerateArray().Select((buffer, index) => {
            var data = buffer.TryGetProperty("uri", out var uri) ? Resource(uri.GetString()!)
                : index == 0 && binary is not null ? binary : throw new InvalidDataException("Missing glTF buffer URI.");
            var length = buffer.GetProperty("byteLength").GetInt32();
            Require(length >= 0 && length <= data.Length && data.Length - length <= 3, "Invalid glTF buffer length.");
            return data.AsSpan(0, length).ToArray();
        }).ToArray();
    }

    public static PbrSceneAsset Read(string path, int maximumTextureSize = 256, string attribution = "",
        MeshPatchBuildSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTextureSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTextureSize, 8192);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(path);
        byte[]? binary = null;
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.AsSpan().StartsWith("glTF"u8)) {
            Require(bytes.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == 2
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == bytes.Length, "Invalid GLB header.");
            var offset = 12;
            json = Chunk(0x4e4f534a);
            if (offset < bytes.Length) { binary = Chunk(0x004e4942).ToArray(); }
            Require(offset == bytes.Length, "Unexpected GLB chunks.");
            ReadOnlyMemory<byte> Chunk(uint type)
            {
                Require(offset <= bytes.Length - 8, "Truncated GLB chunk.");
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)) == type && length % 4 == 0
                    && length <= bytes.Length - offset - 8, "Invalid GLB chunk.");
                var chunk = bytes.AsMemory(offset + 8, (int)length); offset += 8 + (int)length; return chunk;
            }
        }
        using var document = JsonDocument.Parse(json);
        var source = new GltfScene(document.RootElement, Path.GetDirectoryName(Path.GetFullPath(path))!, binary, maximumTextureSize, cancellationToken);
        return source.Cook(attribution, settings ?? MeshPatchBuildSettings.Default);
    }

    private PbrSceneAsset Cook(string attribution, MeshPatchBuildSettings settings)
    {
        var materials = _root.TryGetProperty("materials", out var sourceMaterials)
            ? sourceMaterials.EnumerateArray().Select(Material).ToList() : [];
        var defaultMaterial = materials.Count;
        materials.Add(new(new(float3.one, 1, 1, float3.zero, 1)));
        var geometry = new List<MeshPatchAsset>();
        var instances = new List<PbrSceneInstance>();
        var meshes = new Dictionary<int, (int Geometry, int Material)[]>();
        var nodes = _root.GetProperty("nodes");
        var visited = new HashSet<int>();
        var scene = Item(_root, "scenes", Integer(_root, "scene", 0));
        foreach (var node in scene.GetProperty("nodes").EnumerateArray()) { Visit(node.GetInt32(), Matrix4x4.Identity); }
        Require(instances.Count > 0, "The selected glTF scene contains no mesh instances.");
        if (instances.All(instance => instance.Material != defaultMaterial)) { materials.RemoveAt(defaultMaterial); }
        return PbrSceneAsset.Create(geometry.ToArray(), materials.ToArray(), instances.ToArray(), attribution);

        void Visit(int index, Matrix4x4 parent)
        {
            _cancellation.ThrowIfCancellationRequested();
            Require((uint)index < (uint)nodes.GetArrayLength() && visited.Add(index), "glTF nodes must form a forest without repeated children.");
            var node = nodes[index];
            Reject(node, "skin", "weights", "extensions");
            var transform = Transform(node) * parent;
            if (node.TryGetProperty("mesh", out var meshIndex)) {
                var meshId = meshIndex.GetInt32();
                if (!meshes.TryGetValue(meshId, out var primitives)) {
                    var mesh = Item(_root, "meshes", meshId);
                    Reject(mesh, "weights", "extensions");
                    primitives = mesh.GetProperty("primitives").EnumerateArray().Select(primitive => {
                        var material = Integer(primitive, "material", defaultMaterial);
                        Require((uint)material < (uint)materials.Count, "Invalid primitive material index.");
                        var input = materials[material];
                        var meshData = Geometry(primitive, input.Normal is not null,
                            input.BaseColor is not null || input.Normal is not null || input.MetallicRoughness is not null
                            || input.Occlusion is not null || input.Emissive is not null);
                        var result = (geometry.Count, material);
                        geometry.Add(MeshPatchAsset.Cook(meshData, settings, _cancellation));
                        return result;
                    }).ToArray();
                    meshes.Add(meshId, primitives);
                }
                var matrix = new float4x4(new(transform.M11, transform.M12, transform.M13, transform.M14),
                    new(transform.M21, transform.M22, transform.M23, transform.M24),
                    new(transform.M31, transform.M32, transform.M33, transform.M34),
                    new(transform.M41, transform.M42, transform.M43, transform.M44));
                foreach (var primitive in primitives) { instances.Add(new(primitive.Geometry, primitive.Material, matrix)); }
            }
            if (node.TryGetProperty("children", out var children)) {
                foreach (var child in children.EnumerateArray()) { Visit(child.GetInt32(), transform); }
            }
        }
    }

    private static Matrix4x4 Transform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix)) {
            Require(!node.TryGetProperty("translation", out _) && !node.TryGetProperty("rotation", out _) && !node.TryGetProperty("scale", out _),
                "glTF nodes cannot combine matrix and TRS transforms.");
            var values = Floats(matrix, 16);
            return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
        }
        var t = node.TryGetProperty("translation", out var translation) ? Floats(translation, 3) : [0f, 0f, 0f];
        var s = node.TryGetProperty("scale", out var scale) ? Floats(scale, 3) : [1f, 1f, 1f];
        var r = node.TryGetProperty("rotation", out var rotation) ? Floats(rotation, 4) : [0f, 0f, 0f, 1f];
        var q = new Quaternion(r[0], r[1], r[2], r[3]);
        Require(MathF.Abs(q.LengthSquared() - 1) < 0.001f, "glTF rotations must be unit quaternions.");
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q))
            * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private byte[] Resource(string uri)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (uri.StartsWith("data:", StringComparison.Ordinal)) {
            var comma = uri.IndexOf(',');
            Require(comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.Ordinal), "Only base64 data URIs are supported.");
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        var relative = Uri.UnescapeDataString(uri);
        Require(!Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Contains('?') && !relative.Contains('#'), "glTF resources require local relative paths.");
        var path = Path.GetFullPath(Path.Combine(_directory, relative));
        Require(path.StartsWith(_directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            "glTF resources must stay within the source directory.");
        return File.ReadAllBytes(path);
    }

    private static JsonElement Item(JsonElement owner, string array, int index)
    {
        var items = owner.GetProperty(array);
        Require((uint)index < (uint)items.GetArrayLength(), $"Invalid glTF {array} index.");
        return items[index];
    }

    private static int Integer(JsonElement owner, string name, int fallback) => owner.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    private static float Scalar(JsonElement owner, string name, float fallback) => owner.TryGetProperty(name, out var value) ? value.GetSingle() : fallback;
    private static float[] Floats(JsonElement array, int count)
    {
        Require(array.GetArrayLength() == count, "Invalid glTF vector length.");
        var values = array.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        Require(values.All(float.IsFinite), "glTF vectors must be finite.");
        return values;
    }

    private static void Reject(JsonElement owner, params string[] names)
    {
        foreach (var name in names) {
            if (owner.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)) {
                throw new NotSupportedException($"glTF {name} is not supported by the opaque static scene importer.");
            }
        }
    }

    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
