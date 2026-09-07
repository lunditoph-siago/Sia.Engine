using System.Buffers.Binary;
using System.Text.Json;
using Mikktspace.NET;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class GltfScene
{
    private MeshData Geometry(JsonElement primitive, bool needsTangents, bool needsUv)
    {
        Reject(primitive, "targets", "extensions");
        Require(Integer(primitive, "mode", 4) == 4, "Only triangle glTF primitives are supported.");
        var attributes = primitive.GetProperty("attributes");
        foreach (var attribute in attributes.EnumerateObject()) {
            Require(attribute.Name is "POSITION" or "NORMAL" or "TEXCOORD_0" or "TANGENT", $"Unsupported glTF vertex attribute: {attribute.Name}.");
        }
        var positions = Attribute(attributes.GetProperty("POSITION").GetInt32(), "VEC3", 3);
        var count = positions.Length / 3;
        Require(count > 0, "glTF primitives require vertices.");
        var normals = attributes.TryGetProperty("NORMAL", out var normal) ? Attribute(normal.GetInt32(), "VEC3", 3) : null;
        var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uv) ? Attribute(uv.GetInt32(), "VEC2", 2, allowNormalized: true) : null;
        var tangents = attributes.TryGetProperty("TANGENT", out var tangent) ? Attribute(tangent.GetInt32(), "VEC4", 4) : null;
        Require((normals is null || normals.Length == count * 3) && (uvs is null || uvs.Length == count * 2)
            && (tangents is null || tangents.Length == count * 4), "glTF attribute counts must agree.");
        Require(!needsUv || uvs is not null, "Textured materials require TEXCOORD_0.");
        var indices = primitive.TryGetProperty("indices", out var indexAccessor) ? Indices(indexAccessor.GetInt32())
            : Enumerable.Range(0, count).Select(index => (uint)index).ToArray();
        Require(indices.Length > 0 && indices.Length % 3 == 0 && indices.All(index => index < count), "Invalid glTF triangle indices.");
        var vertices = new MeshVertex[count];
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        for (var i = 0; i < count; i++) {
            var position = new float3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            var n = normals is null ? float3.zero : new float3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            if (normals is not null) { Require(math.lengthsq(n) > 1e-12f, "glTF normals must be nonzero."); n = math.normalize(n); }
            var t = tangents is null ? float4.zero : new float4(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2], tangents[i * 4 + 3]);
            if (tangents is not null) {
                Require(t.w is -1 or 1 && math.lengthsq(t.xyz) > 1e-12f, "glTF tangents require a direction and handedness of -1 or 1.");
                t = new(math.normalize(t.xyz), t.w);
            }
            vertices[i] = new(position, n, uvs is null ? float2.zero : new(uvs[i * 2], uvs[i * 2 + 1])) { Tangent = t };
            minimum = math.min(minimum, position); maximum = math.max(maximum, position);
        }
        if (normals is null || (needsTangents && tangents is null)) {
            var corners = new MeshVertex[indices.Length];
            for (var i = 0; i < indices.Length; i++) { corners[i] = vertices[indices[i]]; }
            vertices = corners;
            indices = Enumerable.Range(0, corners.Length).Select(index => (uint)index).ToArray();
            if (normals is null) {
                for (var i = 0; i < corners.Length; i += 3) {
                    var n = math.cross(corners[i + 1].Position - corners[i].Position, corners[i + 2].Position - corners[i].Position);
                    Require(math.lengthsq(n) > 1e-20f, "Cannot generate normals for a degenerate glTF triangle.");
                    n = math.normalize(n);
                    for (var corner = 0; corner < 3; corner++) { corners[i + corner] = corners[i + corner] with { Normal = n }; }
                }
            }
            if (needsTangents && tangents is null) {
                _cancellation.ThrowIfCancellationRequested();
                var generated = MikkGenerator.GenerateTangentSpace(corners.Length / 3, _ => 3,
                    (int face, int vertex, out float x, out float y, out float z) => { var p = corners[face * 3 + vertex].Position; x = p.x; y = p.y; z = p.z; },
                    (int face, int vertex, out float x, out float y, out float z) => { var n = corners[face * 3 + vertex].Normal; x = n.x; y = n.y; z = n.z; },
                    (int face, int vertex, out float u, out float v) => { var value = corners[face * 3 + vertex].UV; u = value.x; v = value.y; },
                    (face, vertex, x, y, z, sign) => { var index = face * 3 + vertex; corners[index] = corners[index] with { Tangent = new(x, y, z, sign) }; });
                Require(generated, "MikkTSpace could not generate glTF tangents.");
                for (var i = 0; i < corners.Length; i++) {
                    var value = corners[i];
                    var direction = value.Tangent.xyz;
                    Require(float.IsFinite(direction.x) && float.IsFinite(direction.y) && float.IsFinite(direction.z)
                        && value.Tangent.w is -1 or 1, "MikkTSpace produced an invalid tangent.");
                    if (math.lengthsq(direction) <= 1e-12f) {
                        var axis = MathF.Abs(value.Normal.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
                        corners[i] = value with { Tangent = new(math.normalize(math.cross(axis, value.Normal)), value.Tangent.w) };
                    }
                }
                _cancellation.ThrowIfCancellationRequested();
            }
        }
        return new(vertices, indices, new(minimum, maximum));
    }

    private float[] Attribute(int index, string type, int components, bool allowNormalized = false)
    {
        var accessor = Accessor(index, type, components, out var componentType, out var count, out var stride, out var bytes);
        var normalized = accessor.TryGetProperty("normalized", out var flag) && flag.GetBoolean();
        Require(componentType == 5126 && !normalized || allowNormalized && normalized && componentType is 5121 or 5123,
            "glTF attributes require FLOAT, or normalized unsigned UV components.");
        var size = ComponentSize(componentType);
        var values = new float[checked(count * components)];
        for (var item = 0; item < count; item++) {
            for (var component = 0; component < components; component++) {
                var at = bytes.Span[(item * stride + component * size)..];
                var value = componentType switch {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(at),
                    5121 => at[0] / 255f,
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(at) / 65535f,
                    _ => throw new InvalidDataException("Invalid attribute component type.")
                };
                Require(float.IsFinite(value), "glTF attributes must be finite.");
                values[item * components + component] = value;
            }
        }
        return values;
    }

    private uint[] Indices(int index)
    {
        var accessor = Accessor(index, "SCALAR", 1, out var type, out var count, out var stride, out var bytes);
        Require(type is 5121 or 5123 or 5125 && (!accessor.TryGetProperty("normalized", out var flag) || !flag.GetBoolean()),
            "glTF indices require unsigned integers.");
        var values = new uint[count];
        for (var i = 0; i < count; i++) {
            var at = bytes.Span[(i * stride)..];
            values[i] = type switch { 5121 => at[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(at), _ => BinaryPrimitives.ReadUInt32LittleEndian(at) };
        }
        return values;
    }

    private JsonElement Accessor(int index, string type, int components, out int componentType, out int count, out int stride, out ReadOnlyMemory<byte> bytes)
    {
        var accessor = Item(_root, "accessors", index);
        Reject(accessor, "sparse", "extensions");
        Require(accessor.GetProperty("type").GetString() == type, "Unexpected glTF accessor type.");
        componentType = accessor.GetProperty("componentType").GetInt32();
        var size = ComponentSize(componentType);
        count = accessor.GetProperty("count").GetInt32();
        var view = Item(_root, "bufferViews", accessor.GetProperty("bufferView").GetInt32());
        stride = Integer(view, "byteStride", size * components);
        var offset = Integer(accessor, "byteOffset", 0);
        var buffer = BufferView(view);
        Require(count > 0 && count <= 10000000 && stride >= size * components && stride <= 252 && stride % size == 0
            && offset >= 0 && offset % size == 0 && (long)offset + (long)(count - 1) * stride + size * components <= buffer.Length,
            "glTF accessor exceeds its buffer view or has invalid alignment.");
        bytes = buffer[offset..];
        return accessor;
    }

    private ReadOnlyMemory<byte> BufferView(JsonElement view)
    {
        Reject(view, "extensions");
        var index = view.GetProperty("buffer").GetInt32();
        Require((uint)index < (uint)_buffers.Length, "Invalid glTF buffer index.");
        var offset = Integer(view, "byteOffset", 0); var length = view.GetProperty("byteLength").GetInt32();
        Require(offset >= 0 && length >= 0 && (long)offset + length <= _buffers[index].Length, "Invalid glTF buffer view range.");
        return _buffers[index].AsMemory(offset, length);
    }

    private static int ComponentSize(int type) => type switch {
        5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => throw new NotSupportedException("Unsupported glTF accessor component type.")
    };
}
