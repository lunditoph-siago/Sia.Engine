using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

// Algorithm: https://github.com/zeux/meshoptimizer/blob/v1.2/src/clusterizer.cpp
// License: https://github.com/zeux/meshoptimizer/blob/v1.2/LICENSE.md
public static partial class MeshletBuilder
{
    private const int k_CandidateCapacity = 256;
    private const int k_SeedCapacity = 256;
    private const int k_NewSeeds = 4;

    private const float k_DefaultConeWeight = 0.25f;

    public static MeshletData Build(
        MeshData mesh,
        int maxVertices = 64,
        int maxTriangles = 124,
        float coneWeight = k_DefaultConeWeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        return Build(mesh.Vertices, mesh.Indices, maxVertices, maxTriangles, coneWeight, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static MeshletData Build(
        ReadOnlySpan<MeshVertex> vertices,
        ReadOnlySpan<uint> indices,
        int maxVertices = 64,
        int maxTriangles = 124,
        float coneWeight = k_DefaultConeWeight,
        CancellationToken cancellationToken = default)
    {
        ValidateLimits(maxVertices, maxTriangles);
        if (coneWeight is < 0f or > 1f) {
            throw new ArgumentOutOfRangeException(nameof(coneWeight));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (indices.Length % 3 != 0) {
            throw new ArgumentException("Indices must describe complete triangles.", nameof(indices));
        }
        for (var i = 0; i < vertices.Length; i++) {
            if ((i & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var position = vertices[i].Position;
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) {
                throw new ArgumentException("Vertex positions must be finite.", nameof(vertices));
            }
        }
        for (var i = 0; i < indices.Length; i++) {
            if ((i & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (indices[i] >= (uint)vertices.Length) {
                throw new ArgumentException("A triangle references a missing vertex.", nameof(indices));
            }
        }
        if (indices.IsEmpty) {
            return new([], [], [], []);
        }

        var triangleCount = indices.Length / 3;
        var offsets = new int[checked(vertices.Length + 1)];
        for (var triangle = 0; triangle < triangleCount; triangle++) {
            if ((triangle & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var a = indices[triangle * 3];
            var b = indices[triangle * 3 + 1];
            var c = indices[triangle * 3 + 2];
            offsets[a + 1]++;
            if (b != a) {
                offsets[b + 1]++;
            }
            if (c != a && c != b) {
                offsets[c + 1]++;
            }
        }
        for (var i = 1; i < offsets.Length; i++) {
            offsets[i] += offsets[i - 1];
        }
        var cursors = offsets.AsSpan(0, vertices.Length).ToArray();
        var adjacency = new int[offsets[^1]];
        var liveTriangles = new int[vertices.Length];
        for (var i = 0; i < liveTriangles.Length; i++) {
            liveTriangles[i] = offsets[i + 1] - offsets[i];
        }
        var centers = new float3[triangleCount];
        var normals = new float3[triangleCount];
        double meshArea = 0;
        for (var triangle = 0; triangle < triangleCount; triangle++) {
            if ((triangle & 4095) == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var a = indices[triangle * 3];
            var b = indices[triangle * 3 + 1];
            var c = indices[triangle * 3 + 2];
            adjacency[cursors[a]++] = triangle;
            if (b != a) {
                adjacency[cursors[b]++] = triangle;
            }
            if (c != a && c != b) {
                adjacency[cursors[c]++] = triangle;
            }
            var pa = vertices[(int)a].Position;
            var pb = vertices[(int)b].Position;
            var pc = vertices[(int)c].Position;
            centers[triangle] = (pa + pb + pc) / 3f;
            var faceNormal = math.cross(pb - pa, pc - pa);
            var area = math.length(faceNormal);
            meshArea += area;
            normals[triangle] = area == 0 ? float3.zero : faceNormal / area;
        }
        var liveEnd = offsets.AsSpan(1, vertices.Length).ToArray();
        var spatialNodes = BuildSpatialTree(centers, cancellationToken, out var spatialOrder, out var spatialLeaves, out var seedCorner);

        var triangleAreaAverage = triangleCount == 0 ? 0.0 : meshArea / triangleCount * 0.5;
        var expectedRadiusD = System.Math.Sqrt(triangleAreaAverage * maxTriangles) * 0.5;
        var expectedRadius = expectedRadiusD > 0 ? (float)expectedRadiusD : 1f;

        var localVertices = new int[vertices.Length];
        Array.Fill(localVertices, -1);
        var emitted = new bool[triangleCount];
        var candidateMarks = new int[triangleCount];
        var candidates = new int[k_CandidateCapacity];
        Span<int> seeds = stackalloc int[k_SeedCapacity];
        Span<int> newSeeds = stackalloc int[k_NewSeeds];
        var candidateCount = 0;
        var seedCount = 0;
        var generation = 1;
        var vertexIndices = new uint[indices.Length];
        var triangleIndices = new byte[indices.Length];
        var sourceTriangles = new uint[triangleCount];
        var meshlets = new List<Meshlet>();
        var vertexOffset = 0;
        var triangleOffset = 0;
        var vertexCount = 0;
        var count = 0;
        var emittedCount = 0;
        var seed = -1;
        var centerSum = float3.zero;
        var normalSum = float3.zero;

        while (emittedCount < triangleCount) {
            cancellationToken.ThrowIfCancellationRequested();
            var triangle = -1;
            if (count == 0) {
                triangle = seed >= 0 ? seed : FindSpatialTriangle(
                    spatialNodes, spatialOrder, centers, emitted, seedCorner.x, seedCorner.y, seedCorner.z);
            }
            else if (count < maxTriangles) {
                var coneAxis = math.normalizesafe(normalSum);
                var meshletCenter = centerSum / count;
                triangle = SelectCandidate(
                    candidates, ref candidateCount, indices, normals, centers, localVertices, liveTriangles, emitted,
                    maxVertices - vertexCount, meshletCenter, coneAxis, coneWeight, expectedRadius);
                if (triangle < 0 && maxVertices - vertexCount >= 3) {
                    triangle = FindSpatialTriangle(spatialNodes, spatialOrder, centers, emitted,
                        meshletCenter.x, meshletCenter.y, meshletCenter.z);
                }
            }

            if (triangle < 0) {
                var remainingSeeds = 0;
                for (var i = 0; i < seedCount; i++) {
                    if (!emitted[seeds[i]]) {
                        seeds[remainingSeeds++] = seeds[i];
                    }
                }
                seedCount = System.Math.Min(remainingSeeds, seeds.Length - k_NewSeeds);
                var newSeedCount = AppendSeedTriangles(
                    newSeeds, vertexIndices.AsSpan(vertexOffset, vertexCount), indices,
                    offsets, liveEnd, adjacency, liveTriangles, centers, seedCorner);
                for (var i = 0; i < newSeedCount; i++) {
                    var next = newSeeds[i];
                    if (!seeds.Slice(0, seedCount).Contains(next)) {
                        seeds[seedCount++] = next;
                    }
                }
                seed = SelectSeed(seeds.Slice(0, seedCount), indices, centers, liveTriangles, emitted, seedCorner);
                if (seed < 0) {
                    var meshletCenter = centerSum / count;
                    seed = FindSpatialTriangle(spatialNodes, spatialOrder, centers, emitted,
                        meshletCenter.x, meshletCenter.y, meshletCenter.z);
                }
                FinishMeshlet(vertices, vertexIndices, triangleIndices, meshlets,
                    vertexOffset, vertexCount, triangleOffset, count);
                for (var i = 0; i < vertexCount; i++) {
                    localVertices[vertexIndices[vertexOffset + i]] = -1;
                }
                vertexOffset += vertexCount;
                triangleOffset += count * 3;
                vertexCount = 0;
                count = 0;
                candidateCount = 0;
                generation++;
                centerSum = float3.zero;
                normalSum = float3.zero;
                continue;
            }

            emitted[triangle] = true;
            emittedCount++;
            RemoveSpatialTriangle(spatialNodes, spatialLeaves[triangle]);
            var cornerA = indices[triangle * 3];
            var cornerB = indices[triangle * 3 + 1];
            var cornerC = indices[triangle * 3 + 2];
            RemoveFromAdjacency(triangle, (int)cornerA, offsets, liveEnd, adjacency);
            if (cornerB != cornerA) {
                RemoveFromAdjacency(triangle, (int)cornerB, offsets, liveEnd, adjacency);
            }
            if (cornerC != cornerA && cornerC != cornerB) {
                RemoveFromAdjacency(triangle, (int)cornerC, offsets, liveEnd, adjacency);
            }
            for (var corner = 0; corner < 3; corner++) {
                var index = indices[triangle * 3 + corner];
                if (corner == 0 || (index != cornerA && (corner == 1 || index != cornerB))) {
                    liveTriangles[index]--;
                }
                var local = localVertices[index];
                if (local < 0) {
                    local = vertexCount++;
                    localVertices[index] = local;
                    vertexIndices[vertexOffset + local] = index;
                    AddCandidates((int)index, offsets, liveEnd, adjacency, candidateMarks,
                        generation, ref candidates, ref candidateCount);
                }
                triangleIndices[triangleOffset + count * 3 + corner] = (byte)local;
            }
            sourceTriangles[triangleOffset / 3 + count] = (uint)triangle;
            centerSum += centers[triangle];
            normalSum += normals[triangle];
            count++;
        }

        FinishMeshlet(vertices, vertexIndices, triangleIndices, meshlets,
            vertexOffset, vertexCount, triangleOffset, count);
        Array.Resize(ref vertexIndices, vertexOffset + vertexCount);
        cancellationToken.ThrowIfCancellationRequested();
        return new(meshlets.ToArray(), vertexIndices, triangleIndices, sourceTriangles);
    }

    public static MeshletData[] BuildMany(
        IReadOnlyList<MeshData> meshes,
        int maxVertices = 64,
        int maxTriangles = 124,
        float coneWeight = k_DefaultConeWeight,
        int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ValidateLimits(maxVertices, maxTriangles);
        if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1) {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = meshes.ToArray();
        var results = new MeshletData[inputs.Length];
        Parallel.For(0, inputs.Length, new ParallelOptions {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        }, i => results[i] = Build(inputs[i], maxVertices, maxTriangles, coneWeight, cancellationToken));
        return results;
    }

    private static void ValidateLimits(int maxVertices, int maxTriangles)
    {
        if (maxVertices is < 3 or > 256) {
            throw new ArgumentOutOfRangeException(nameof(maxVertices));
        }
        if (maxTriangles is < 1 or > 512) {
            throw new ArgumentOutOfRangeException(nameof(maxTriangles));
        }
    }

    private static void RemoveFromAdjacency(int triangle, int vertex, int[] offsets, int[] liveEnd, int[] adjacency)
    {
        var start = offsets[vertex];
        var end = liveEnd[vertex];
        var slot = Array.IndexOf(adjacency, triangle, start, end - start);
        if (slot >= 0) {
            adjacency[slot] = adjacency[end - 1];
            liveEnd[vertex] = end - 1;
        }
    }

    private static void AddCandidates(
        int vertex, int[] offsets, int[] liveEnd, int[] adjacency,
        int[] marks, int generation, ref int[] candidates, ref int count)
    {
        var end = liveEnd[vertex];
        for (var i = offsets[vertex]; i < end; i++) {
            var triangle = adjacency[i];
            if (marks[triangle] != generation) {
                marks[triangle] = generation;
                if (count == candidates.Length) {
                    Array.Resize(ref candidates, candidates.Length * 2);
                }
                candidates[count++] = triangle;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float MeshletScore(float distance, float spread, float coneWeight, float expectedRadius)
    {
        var cone = 1f - spread * coneWeight;
        var coneClamped = cone < 1e-3f ? 1e-3f : cone;
        return (1f + distance / expectedRadius * (1f - coneWeight)) * coneClamped;
    }

    private static int SelectCandidate(
        int[] candidates, ref int count, ReadOnlySpan<uint> indices, float3[] normals, float3[] centers,
        int[] localVertices, int[] liveTriangles, bool[] emitted, int availableVertices,
        float3 meshletCenter, float3 coneAxis, float coneWeight, float expectedRadius)
    {
        var best = -1;
        var bestSlot = -1;
        var bestPriority = int.MaxValue;
        var bestScore = float.PositiveInfinity;
        for (var i = 0; i < count; i++) {
            var triangle = candidates[i];
            if (emitted[triangle]) {
                candidates[i--] = candidates[--count];
                continue;
            }
            var a = indices[triangle * 3];
            var b = indices[triangle * 3 + 1];
            var c = indices[triangle * 3 + 2];
            var extra = (localVertices[a] < 0 ? 1 : 0)
                + (b != a && localVertices[b] < 0 ? 1 : 0)
                + (c != a && c != b && localVertices[c] < 0 ? 1 : 0);
            if (extra > availableVertices) {
                continue;
            }
            var priority = 2 + extra;
            if (extra == 0) {
                priority = 0;
            }
            else if (liveTriangles[a] == 1 || liveTriangles[b] == 1 || liveTriangles[c] == 1) {
                priority = 1;
            }
            else if ((liveTriangles[a] == 2 ? 1 : 0) + (b != a && liveTriangles[b] == 2 ? 1 : 0)
                + (c != a && c != b && liveTriangles[c] == 2 ? 1 : 0) >= 2) {
                priority = 1 + extra;
            }
            if (priority > bestPriority) {
                continue;
            }
            var distance = math.distance(centers[triangle], meshletCenter);
            var spread = math.dot(normals[triangle], coneAxis);
            var score = MeshletScore(distance, spread, coneWeight, expectedRadius);
            if (priority < bestPriority || score < bestScore) {
                best = triangle;
                bestSlot = i;
                bestPriority = priority;
                bestScore = score;
            }
        }
        if (best >= 0) {
            var slot = bestSlot < count && candidates[bestSlot] == best ? bestSlot : Array.IndexOf(candidates, best, 0, count);
            if (slot >= 0) {
                candidates[slot] = candidates[--count];
            }
        }
        return best;
    }

    private static int AppendSeedTriangles(
        Span<int> outSeeds, ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<uint> indices,
        int[] offsets, int[] liveEnd, int[] adjacency, int[] liveTriangles, float3[] centers, float3 corner)
    {
        Span<int> bestSeeds = stackalloc int[k_NewSeeds];
        Span<long> bestLive = stackalloc long[k_NewSeeds];
        Span<float> bestScore = stackalloc float[k_NewSeeds];
        for (var i = 0; i < k_NewSeeds; i++) {
            bestSeeds[i] = -1;
            bestLive[i] = long.MaxValue;
            bestScore[i] = float.PositiveInfinity;
        }
        foreach (var vertex in meshletVertices) {
            var index = (int)vertex;
            var bestNeighbor = -1;
            var bestNeighborLive = long.MaxValue;
            var end = liveEnd[index];
            for (var i = offsets[index]; i < end; i++) {
                var triangle = adjacency[i];
                var a = indices[triangle * 3];
                var b = indices[triangle * 3 + 1];
                var c = indices[triangle * 3 + 2];
                var live = (long)liveTriangles[a] + liveTriangles[b] + liveTriangles[c];
                if (live < bestNeighborLive) {
                    bestNeighbor = triangle;
                    bestNeighborLive = live;
                }
            }
            if (bestNeighbor < 0) {
                continue;
            }
            var score = math.distance(centers[bestNeighbor], corner);
            for (var i = 0; i < k_NewSeeds; i++) {
                if (bestNeighborLive < bestLive[i] || (bestNeighborLive == bestLive[i] && score <= bestScore[i])) {
                    bestSeeds[i] = bestNeighbor;
                    bestLive[i] = bestNeighborLive;
                    bestScore[i] = score;
                    break;
                }
            }
        }
        var count = 0;
        for (var i = 0; i < k_NewSeeds; i++) {
            if (bestSeeds[i] >= 0) {
                outSeeds[count++] = bestSeeds[i];
            }
        }
        return count;
    }

    private static int SelectSeed(
        ReadOnlySpan<int> candidates, ReadOnlySpan<uint> indices, float3[] centers,
        int[] liveTriangles, bool[] emitted, float3 corner)
    {
        var best = -1;
        var bestLive = long.MaxValue;
        var bestDistance = float.PositiveInfinity;
        foreach (var triangle in candidates) {
            if (emitted[triangle]) {
                continue;
            }
            var a = indices[triangle * 3];
            var b = indices[triangle * 3 + 1];
            var c = indices[triangle * 3 + 2];
            var live = (long)liveTriangles[a] + (b != a ? liveTriangles[b] : 0)
                + (c != a && c != b ? liveTriangles[c] : 0);
            var distance = math.distancesq(centers[triangle], corner);
            if (live < bestLive || (live == bestLive && (distance < bestDistance || (distance == bestDistance && triangle < best)))) {
                best = triangle;
                bestLive = live;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static void FinishMeshlet(
        ReadOnlySpan<MeshVertex> vertices, uint[] vertexIndices, byte[] triangleIndices,
        List<Meshlet> meshlets, int vertexOffset, int vertexCount, int triangleOffset, int triangleCount)
    {
        var bounds = ComputeBounds(vertices, vertexIndices.AsSpan(vertexOffset, vertexCount),
            triangleIndices.AsSpan(triangleOffset, triangleCount * 3));
        meshlets.Add(new(vertexOffset, vertexCount, triangleOffset, triangleCount, bounds));
    }
}
