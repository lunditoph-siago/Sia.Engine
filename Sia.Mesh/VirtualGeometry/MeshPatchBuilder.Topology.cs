namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    private static MeshData Prepare(MeshData mesh, CancellationToken cancellationToken, out int removed)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        cancellationToken.ThrowIfCancellationRequested();
        if (mesh.Indices.Length % 3 != 0) { throw new ArgumentException("Indices must describe complete triangles.", nameof(mesh)); }
        var vertices = new List<MeshVertex>();
        var identities = new Dictionary<VertexIdentity, uint>();
        var remap = new uint[mesh.Vertices.Length];
        for (var i = 0; i < mesh.Vertices.Length; i++) {
            if ((i & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            var v = mesh.Vertices[i];
            var identity = Identity(v);
            if (!float.IsFinite(identity.X) || !float.IsFinite(identity.Y) || !float.IsFinite(identity.Z)
                || !float.IsFinite(identity.Nx) || !float.IsFinite(identity.Ny) || !float.IsFinite(identity.Nz)
                || !float.IsFinite(identity.U) || !float.IsFinite(identity.V) || !v.HasFiniteTangent) {
                throw new ArgumentException("Mesh positions and attributes must be finite.", nameof(mesh));
            }
            if (!identities.TryGetValue(identity, out remap[i])) {
                identities.Add(identity, remap[i] = (uint)vertices.Count);
                vertices.Add(v);
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
            if (!faces.Add(Face((int)a, (int)b, (int)c))) { throw new ArgumentException("Mesh contains duplicate faces.", nameof(mesh)); }
            indices.AddRange([a, b, c]);
        }
        var result = Compact(vertices.ToArray(), indices.ToArray());
        _ = Topology(result, cancellationToken);
        return result;
    }

    private static (Triangle[] Triangles, HashSet<int>[] Incident, bool[] Locked) Topology(MeshData mesh,
        CancellationToken cancellationToken)
    {
        var triangles = new Triangle[mesh.Indices.Length / 3];
        var incident = new HashSet<int>[mesh.Vertices.Length];
        for (var i = 0; i < incident.Length; i++) { incident[i] = []; }
        var edges = new Dictionary<(int, int), (int Count, int Winding, int First, int Second)>();
        for (var t = 0; t < triangles.Length; t++) {
            if ((t & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            var a = (int)mesh.Indices[t * 3];
            var b = (int)mesh.Indices[t * 3 + 1];
            var c = (int)mesh.Indices[t * 3 + 2];
            triangles[t] = new(a, b, c);
            incident[a].Add(t); incident[b].Add(t); incident[c].Add(t);
            Add(a, b, t); Add(b, c, t); Add(c, a, t);
        }
        var locked = new bool[mesh.Vertices.Length];
        foreach (var (edge, uses) in edges) {
            if (uses.Count > 2 || (uses.Count == 2 && uses.Winding != 0)) {
                throw new ArgumentException("Mesh requires manifold edges with consistent winding.", nameof(mesh));
            }
            if (uses.Count == 1) { locked[edge.Item1] = true; locked[edge.Item2] = true; }
        }
        for (var v = 0; v < incident.Length; v++) {
            if ((v & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (locked[v] || incident[v].Count == 0) { continue; }
            var remaining = new HashSet<int>(incident[v]);
            var pending = new Stack<int>();
            pending.Push(remaining.First());
            var visited = 0;
            while (pending.TryPop(out var t)) {
                if (!remaining.Remove(t)) { continue; }
                if ((visited++ & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                var face = triangles[t];
                for (var corner = 0; corner < 3; corner++) {
                    var neighbor = face[corner];
                    if (neighbor == v) { continue; }
                    var edge = edges[Edge(v, neighbor)];
                    var connected = edge.First == t ? edge.Second : edge.First;
                    if (connected >= 0 && remaining.Contains(connected)) { pending.Push(connected); }
                }
            }
            if (remaining.Count != 0) { locked[v] = true; }
        }
        return (triangles, incident, locked);

        void Add(int a, int b, int triangle)
        {
            var edge = Edge(a, b);
            var uses = edges.GetValueOrDefault(edge);
            edges[edge] = (uses.Count + 1, uses.Winding + (a < b ? 1 : -1),
                uses.Count == 0 ? triangle : uses.First, uses.Count == 0 ? -1 : triangle);
        }
    }

    private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
    private static (int, int, int) Face(int a, int b, int c)
    {
        if (a > b) { (a, b) = (b, a); }
        if (b > c) { (b, c) = (c, b); }
        if (a > b) { (a, b) = (b, a); }
        return (a, b, c);
    }

    private readonly record struct Triangle(int A, int B, int C)
    {
        public int this[int corner] => corner switch { 0 => A, 1 => B, _ => C };
        public bool Contains(int vertex) => A == vertex || B == vertex || C == vertex;
        public Triangle Replace(int from, int to) => new(A == from ? to : A, B == from ? to : B, C == from ? to : C);
    }
}
