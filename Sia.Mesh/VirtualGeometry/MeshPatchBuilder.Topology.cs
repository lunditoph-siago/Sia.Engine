namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    private static MeshData Prepare(MeshData mesh, CancellationToken cancellationToken, out int removed)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        mesh.ValidateBakeUvs();
        cancellationToken.ThrowIfCancellationRequested();
        if (mesh.Indices.Length % 3 != 0) { throw new ArgumentException("Indices must describe complete triangles.", nameof(mesh)); }
        var vertices = new List<MeshVertex>();
        var bakeUvs = new List<Sia.Math.float2>();
        var identities = new Dictionary<MeshVertexIdentity, uint>();
        var remap = new uint[mesh.Vertices.Length];
        for (var i = 0; i < mesh.Vertices.Length; i++) {
            if ((i & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            var v = mesh.Vertices[i];
            var bakeUv = mesh.HasBakeUvs ? mesh.BakeUvs[i] : default;
            var identity = MeshVertexIdentity.Create(v, bakeUv);
            if (!float.IsFinite(identity.X) || !float.IsFinite(identity.Y) || !float.IsFinite(identity.Z)
                || !float.IsFinite(identity.Nx) || !float.IsFinite(identity.Ny) || !float.IsFinite(identity.Nz)
                || !float.IsFinite(identity.U) || !float.IsFinite(identity.V) || !v.HasFiniteTangent) {
                throw new ArgumentException("Mesh positions and attributes must be finite.", nameof(mesh));
            }
            if (!identities.TryGetValue(identity, out remap[i])) {
                identities.Add(identity, remap[i] = (uint)vertices.Count);
                vertices.Add(v);
                if (mesh.HasBakeUvs) bakeUvs.Add(bakeUv);
            }
        }
        removed = 0;
        var indices = new List<uint>();
        var faces = new HashSet<(int, int, int)>();
        for (var t = 0; t < mesh.Indices.Length; t += 3) {
            if ((t & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (mesh.Indices[t] >= remap.Length || mesh.Indices[t + 1] >= remap.Length || mesh.Indices[t + 2] >= remap.Length) {
                throw new ArgumentException("A triangle references a missing vertex.", nameof(mesh));
            }
            var a = remap[mesh.Indices[t]];
            var b = remap[mesh.Indices[t + 1]];
            var c = remap[mesh.Indices[t + 2]];
            var cross = Sia.Math.math.cross(Position(vertices[(int)b]) - Position(vertices[(int)a]),
                Position(vertices[(int)c]) - Position(vertices[(int)a]));
            if (a == b || a == c || b == c || Sia.Math.math.dot(cross, cross) == 0) { removed++; continue; }
            if (!faces.Add(MeshSimplifier.Face((int)a, (int)b, (int)c))) { throw new ArgumentException("Mesh contains duplicate faces.", nameof(mesh)); }
            indices.AddRange([a, b, c]);
        }
        var result = Compact(vertices.ToArray(), indices.ToArray(), bakeUvs.ToArray(), cancellationToken: cancellationToken);
        MeshSimplifier.ValidateTopology(result, cancellationToken);
        return result;
    }

}
