using Sia.Math;

namespace Sia.Engine.Mesh;

internal static partial class MeshSimplifier
{
    internal static void ValidateTopology(MeshData mesh, CancellationToken token) => _ = Topology(mesh, token);
    internal static double3 Position(MeshVertex vertex) => new(vertex.Position.x, vertex.Position.y, vertex.Position.z);

    private static (Triangle[] Triangles, HashSet<int>[] Incident, bool[] Locked) Topology(MeshData mesh,
        CancellationToken cancellationToken, bool strictManifold = true)
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
                if (strictManifold) throw new ArgumentException("Mesh requires manifold edges with consistent winding.", nameof(mesh));
                locked[edge.Item1] = true; locked[edge.Item2] = true;
                foreach (var triangle in incident[edge.Item1]) {
                    var face = triangles[triangle];
                    if (face.Contains(edge.Item2)) locked[face.A] = locked[face.B] = locked[face.C] = true;
                }
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
    internal static (int, int, int) Face(int a, int b, int c)
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
