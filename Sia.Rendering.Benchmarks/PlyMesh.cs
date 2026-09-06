using System.Globalization;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal static class PlyMesh
{
    public static MeshData Read(TextReader input)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidDataException(message); }
        }
        string[] ReadFields() => (input.ReadLine() ?? throw new InvalidDataException("Unexpected end of PLY input."))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Require(input.ReadLine() == "ply" && input.ReadLine() == "format ascii 1.0", "Expected an ASCII 1.0 PLY mesh.");
        var vertexCount = -1;
        var faceCount = -1;
        var element = "";
        var properties = new List<string>();
        var faceProperty = false;
        while (true) {
            var fields = ReadFields();
            Require(fields.Length != 0, "Empty PLY header line.");
            if (fields[0] == "end_header") { Require(fields.Length == 1, "Invalid PLY header terminator."); break; }
            if (fields[0] is "comment" or "obj_info") { continue; }
            if (fields is ["element", "vertex", var count] && vertexCount < 0 && faceCount < 0) {
                vertexCount = int.Parse(count, CultureInfo.InvariantCulture);
                Require(vertexCount > 0, "PLY requires vertices.");
                element = "vertex";
            }
            else if (fields is ["element", "face", var faces] && vertexCount > 0 && faceCount < 0) {
                faceCount = int.Parse(faces, CultureInfo.InvariantCulture);
                Require(faceCount > 0, "PLY requires faces.");
                element = "face";
            }
            else if (element == "vertex" && fields is ["property", var type, var name]) {
                Require(type is "float" or "float32" or "double" or "float64" or "uchar" or "uint8" or "int" or "int32",
                    "Unsupported PLY vertex property type.");
                Require(!properties.Contains(name), "Duplicate PLY vertex property.");
                properties.Add(name);
            }
            else if (element == "face" && !faceProperty
                && fields is ["property", "list", "uchar" or "uint8", "int" or "int32" or "uint" or "uint32", "vertex_indices" or "vertex_index"]) {
                faceProperty = true;
            }
            else { throw new InvalidDataException("Expected vertex scalar properties followed by triangle face indices."); }
        }
        var x = properties.IndexOf("x");
        var y = properties.IndexOf("y");
        var z = properties.IndexOf("z");
        Require(vertexCount > 0 && faceCount > 0 && faceProperty && x >= 0 && y >= 0 && z >= 0, "Incomplete PLY geometry header.");
        var positions = new float3[vertexCount];
        for (var i = 0; i < positions.Length; i++) {
            var fields = ReadFields();
            Require(fields.Length == properties.Count, "PLY vertex property count mismatch.");
            var position = new float3(float.Parse(fields[x], CultureInfo.InvariantCulture),
                float.Parse(fields[y], CultureInfo.InvariantCulture), float.Parse(fields[z], CultureInfo.InvariantCulture));
            Require(float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z), "Non-finite PLY position.");
            positions[i] = position;
        }
        var indices = new uint[checked(faceCount * 3)];
        var used = new bool[vertexCount];
        for (var i = 0; i < faceCount; i++) {
            var fields = ReadFields();
            Require(fields.Length == 4 && fields[0] == "3", "Only triangular PLY faces are supported.");
            for (var j = 0; j < 3; j++) {
                var index = uint.Parse(fields[j + 1], CultureInfo.InvariantCulture);
                Require(index < vertexCount, "PLY face index is out of range.");
                indices[i * 3 + j] = index;
                used[index] = true;
            }
        }
        Require(string.IsNullOrWhiteSpace(input.ReadToEnd()), "Unexpected data after PLY faces.");
        var min = new float3(float.PositiveInfinity);
        var max = new float3(float.NegativeInfinity);
        for (var i = 0; i < positions.Length; i++) {
            if (used[i]) { min = math.min(min, positions[i]); max = math.max(max, positions[i]); }
        }
        var extent = max - min;
        var diameter = System.Math.Max(extent.x, System.Math.Max(extent.y, extent.z));
        Require(float.IsFinite(diameter) && diameter > 0, "PLY mesh has no finite extent.");
        var center = min + extent * 0.5f;
        var vertices = new MeshVertex[used.Count(value => value)];
        var remap = new uint[positions.Length];
        var next = 0;
        for (var i = 0; i < positions.Length; i++) {
            if (!used[i]) { continue; }
            remap[i] = (uint)next;
            vertices[next++] = new((positions[i] - center) * (1.6f / diameter), float3.zero, float2.zero);
        }
        var normals = new float3[vertices.Length];
        for (var i = 0; i < indices.Length; i++) { indices[i] = remap[indices[i]]; }
        for (var i = 0; i < indices.Length; i += 3) {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var normal = math.cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        min = new(float.PositiveInfinity);
        max = new(float.NegativeInfinity);
        for (var i = 0; i < vertices.Length; i++) {
            var length = math.length(normals[i]);
            vertices[i] = vertices[i] with { Normal = length > 0 ? normals[i] / length : new float3(0, 1, 0) };
            min = math.min(min, vertices[i].Position); max = math.max(max, vertices[i].Position);
        }
        return new(vertices, indices, new(min, max));
    }
}
