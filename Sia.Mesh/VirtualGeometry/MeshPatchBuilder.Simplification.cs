using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class MeshPatchBuilder
{
    private static (MeshData Geometry, float Error) Simplify(MeshData mesh, int target,
        MeshPatchBuildSettings settings, CancellationToken cancellationToken)
    {
        var (triangles, incident, locked) = Topology(mesh, cancellationToken);
        var deleted = new bool[triangles.Length];
        var revisions = new int[mesh.Vertices.Length];
        var radii = new float[mesh.Vertices.Length];
        var nextMember = new int[mesh.Vertices.Length];
        Array.Fill(nextMember, -1);
        var lastMember = Enumerable.Range(0, mesh.Vertices.Length).ToArray();
        var positions = mesh.Vertices.Select(Position).ToArray();
        var quadrics = BuildQuadrics(mesh, triangles, locked, settings, cancellationToken, out var coordinates);
        var candidates = new PriorityQueue<Collapse, (double Cost, double Length, int From, int To)>();
        var edges = new HashSet<(int, int)>();
        foreach (var face in triangles) {
            edges.Add(Edge(face.A, face.B)); edges.Add(Edge(face.B, face.C)); edges.Add(Edge(face.C, face.A));
        }
        foreach (var (a, b) in edges) { Enqueue(a, b); Enqueue(b, a); }
        var remaining = triangles.Length;
        while (remaining - 2 >= target && candidates.TryDequeue(out var collapse, out _)) {
            cancellationToken.ThrowIfCancellationRequested();
            var (from, to, fromRevision, toRevision) = collapse;
            if (fromRevision != revisions[from] || toRevision != revisions[to] || incident[from].Count == 0
                || incident[to].Count == 0 || locked[from]) { continue; }
            if (!CanCollapse(from, to, triangles, incident, positions, mesh.Vertices)) { continue; }
            var affected = new HashSet<int>();
            foreach (var t in incident[from].Concat(incident[to])) {
                var face = triangles[t];
                affected.Add(face.A); affected.Add(face.B); affected.Add(face.C);
            }
            foreach (var t in incident[from].ToArray()) {
                var face = triangles[t];
                incident[face.A].Remove(t); incident[face.B].Remove(t); incident[face.C].Remove(t);
                if (face.Contains(to)) { deleted[t] = true; remaining--; }
                else {
                    face = face.Replace(from, to);
                    triangles[t] = face;
                    incident[face.A].Add(t); incident[face.B].Add(t); incident[face.C].Add(t);
                }
            }
            for (var i = 0; i < k_QuadricSize; i++) { quadrics[to * k_QuadricSize + i] += quadrics[from * k_QuadricSize + i]; }
            var radius = (double)radii[to];
            for (var member = from; member >= 0; member = nextMember[member]) {
                radius = System.Math.Max(radius, math.length(positions[member] - positions[to]));
            }
            radii[to] = RoundUp(radius);
            nextMember[lastMember[to]] = from;
            lastMember[to] = lastMember[from];
            edges.Clear();
            foreach (var v in affected) {
                revisions[v]++;
                foreach (var t in incident[v]) {
                    var face = triangles[t];
                    edges.Add(Edge(face.A, face.B)); edges.Add(Edge(face.B, face.C)); edges.Add(Edge(face.C, face.A));
                }
            }
            foreach (var (a, b) in edges) { Enqueue(a, b); Enqueue(b, a); }
        }
        var indices = new uint[remaining * 3];
        var offset = 0;
        for (var t = 0; t < triangles.Length; t++) {
            if (deleted[t]) { continue; }
            indices[offset++] = (uint)triangles[t].A;
            indices[offset++] = (uint)triangles[t].B;
            indices[offset++] = (uint)triangles[t].C;
        }
        var error = 0f;
        for (var v = 0; v < incident.Length; v++) {
            if (incident[v].Count != 0) { error = MathF.Max(error, radii[v]); }
        }
        return (Compact(mesh.Vertices, indices), error);

        void Enqueue(int from, int to)
        {
            if (locked[from] || incident[from].Count == 0 || incident[to].Count == 0) { return; }
            var point = coordinates.AsSpan(to * k_CoordinateCount, k_CoordinateCount);
            var cost = Evaluate(quadrics.AsSpan(from * k_QuadricSize, k_QuadricSize), point)
                + Evaluate(quadrics.AsSpan(to * k_QuadricSize, k_QuadricSize), point);
            if (!double.IsFinite(cost)) { return; }
            candidates.Enqueue(new(from, to, revisions[from], revisions[to]),
                (System.Math.Max(0, cost), math.lengthsq(positions[from] - positions[to]), from, to));
        }
    }

    private static bool CanCollapse(int from, int to, Triangle[] triangles, HashSet<int>[] incident,
        double3[] positions, MeshVertex[] vertices)
    {
        var fromNeighbors = Neighbors(from);
        var toNeighbors = Neighbors(to);
        var common = 0;
        foreach (var vertex in fromNeighbors) { if (toNeighbors.Contains(vertex)) { common++; } }
        if (common != 2 || incident[from].Count(t => triangles[t].Contains(to)) != 2) { return false; }
        var faces = new HashSet<(int, int, int)>();
        foreach (var t in incident[to]) {
            var face = triangles[t];
            if (!face.Contains(from)) { faces.Add(Face(face.A, face.B, face.C)); }
        }
        foreach (var t in incident[from]) {
            var before = triangles[t];
            if (before.Contains(to)) { continue; }
            var after = before.Replace(from, to);
            if (!faces.Add(Face(after.A, after.B, after.C))) { return false; }
            var oldNormal = math.cross(positions[before.B] - positions[before.A], positions[before.C] - positions[before.A]);
            var newNormal = math.cross(positions[after.B] - positions[after.A], positions[after.C] - positions[after.A]);
            if (math.dot(oldNormal, newNormal) <= 0) { return false; }
            var oldArea = UVArea(before);
            if (oldArea != 0 && oldArea * UVArea(after) <= 0) { return false; }
        }
        return faces.Count != 0;

        HashSet<int> Neighbors(int v)
        {
            var neighbors = new HashSet<int>();
            foreach (var t in incident[v]) {
                var face = triangles[t];
                neighbors.Add(face.A); neighbors.Add(face.B); neighbors.Add(face.C);
            }
            neighbors.Remove(v);
            return neighbors;
        }

        double UVArea(Triangle face)
        {
            var a = vertices[face.A].UV;
            var b = vertices[face.B].UV;
            var c = vertices[face.C].UV;
            return ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
        }
    }

    private static float RoundUp(double value)
    {
        if (value == 0) { return 0; }
        var result = MathF.BitIncrement((float)value);
        if (!float.IsFinite(result)) { throw new ArgumentException("Spatial error exceeds the supported finite range."); }
        return result;
    }

    private readonly record struct Collapse(int From, int To, int FromRevision, int ToRevision);
}
