using Sia.Math;

namespace Sia.Engine.Mesh;

// Algorithm: https://github.com/zeux/meshoptimizer/blob/v1.2/src/clusterizer.cpp
// License: https://github.com/zeux/meshoptimizer/blob/v1.2/LICENSE.md
public static partial class MeshletBuilder
{
    private const int k_CandidateCapacity = 256;
    private const int k_SeedCapacity = 32;
    private const int k_NewSeeds = 4;

    public static MeshletData Build(
        MeshData mesh,
        int maxVertices = 64,
        int maxTriangles = 124,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(mesh.Vertices);
        ArgumentNullException.ThrowIfNull(mesh.Indices);
        return Build(mesh.Vertices, mesh.Indices, maxVertices, maxTriangles, cancellationToken);
    }

    public static MeshletData Build(
        ReadOnlySpan<MeshVertex> vertices,
        ReadOnlySpan<uint> indices,
        int maxVertices = 64,
        int maxTriangles = 124,
        CancellationToken cancellationToken = default)
    {
        ValidateLimits(maxVertices, maxTriangles);
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
            centers[triangle] = new float3(
                (float)(((double)pa.x + pb.x + pc.x) / 3),
                (float)(((double)pa.y + pb.y + pc.y) / 3),
                (float)(((double)pa.z + pb.z + pc.z) / 3));
        }
        offsets.AsSpan(0, vertices.Length).CopyTo(cursors);
        var spatialNodes = BuildSpatialTree(centers, cancellationToken, out var spatialOrder, out var spatialLeaves, out var seedCorner);

        var localVertices = new int[vertices.Length];
        Array.Fill(localVertices, -1);
        var emitted = new bool[triangleCount];
        var candidateMarks = new int[triangleCount];
        Span<int> candidates = stackalloc int[k_CandidateCapacity];
        Span<int> seeds = stackalloc int[k_SeedCapacity];
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
        double centerX = 0, centerY = 0, centerZ = 0;

        while (emittedCount < triangleCount) {
            cancellationToken.ThrowIfCancellationRequested();
            var triangle = -1;
            if (count == 0) {
                triangle = seed >= 0 ? seed : FindSpatialTriangle(
                    spatialNodes, spatialOrder, centers, emitted, seedCorner.x, seedCorner.y, seedCorner.z);
            }
            else if (count < maxTriangles) {
                triangle = SelectCandidate(
                    candidates, ref candidateCount, indices, centers, localVertices, liveTriangles, emitted,
                    maxVertices - vertexCount, centerX / count, centerY / count, centerZ / count);
                if (triangle < 0 && maxVertices - vertexCount >= 3) {
                    triangle = FindSpatialTriangle(spatialNodes, spatialOrder, centers, emitted,
                        centerX / count, centerY / count, centerZ / count);
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
                var addedSeeds = 0;
                while (candidateCount > 0 && addedSeeds < k_NewSeeds) {
                    var next = SelectSeed(candidates.Slice(0, candidateCount), indices, centers, liveTriangles, emitted, seedCorner);
                    if (next < 0) {
                        break;
                    }
                    var slot = candidates.Slice(0, candidateCount).IndexOf(next);
                    candidates[slot] = candidates[--candidateCount];
                    if (!seeds.Slice(0, seedCount).Contains(next)) {
                        seeds[seedCount++] = next;
                        addedSeeds++;
                    }
                }
                seed = SelectSeed(seeds.Slice(0, seedCount), indices, centers, liveTriangles, emitted, seedCorner);
                if (seed < 0) {
                    seed = FindSpatialTriangle(spatialNodes, spatialOrder, centers, emitted,
                        centerX / count, centerY / count, centerZ / count);
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
                centerX = centerY = centerZ = 0;
                continue;
            }

            emitted[triangle] = true;
            emittedCount++;
            RemoveSpatialTriangle(spatialNodes, spatialLeaves[triangle]);
            for (var corner = 0; corner < 3; corner++) {
                var index = indices[triangle * 3 + corner];
                if (corner == 0 || (index != indices[triangle * 3] && (corner == 1 || index != indices[triangle * 3 + 1]))) {
                    liveTriangles[index]--;
                }
                var local = localVertices[index];
                if (local < 0) {
                    local = vertexCount++;
                    localVertices[index] = local;
                    vertexIndices[vertexOffset + local] = index;
                    AddCandidates((int)index, offsets, cursors, adjacency, emitted, candidateMarks,
                        generation, candidates, ref candidateCount);
                }
                triangleIndices[triangleOffset + count * 3 + corner] = (byte)local;
            }
            sourceTriangles[triangleOffset / 3 + count] = (uint)triangle;
            centerX += centers[triangle].x;
            centerY += centers[triangle].y;
            centerZ += centers[triangle].z;
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
        }, i => results[i] = Build(inputs[i], maxVertices, maxTriangles, cancellationToken));
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

    private static void AddCandidates(
        int vertex, int[] offsets, int[] cursors, int[] adjacency, bool[] emitted,
        int[] marks, int generation, Span<int> candidates, ref int count)
    {
        var end = offsets[vertex + 1];
        var start = cursors[vertex];
        while (start < end && emitted[adjacency[start]]) {
            start++;
        }
        cursors[vertex] = start;
        var limit = System.Math.Min(end, (long)start + k_CandidateCapacity);
        for (var i = start; i < limit && count < candidates.Length; i++) {
            var triangle = adjacency[i];
            if (!emitted[triangle] && marks[triangle] != generation) {
                marks[triangle] = generation;
                candidates[count++] = triangle;
            }
        }
    }

    private static int SelectCandidate(
        Span<int> candidates, ref int count, ReadOnlySpan<uint> indices, float3[] centers,
        int[] localVertices, int[] liveTriangles, bool[] emitted, int availableVertices,
        double centerX, double centerY, double centerZ)
    {
        var best = -1;
        var bestSlot = -1;
        var bestPriority = int.MaxValue;
        var bestDistance = double.PositiveInfinity;
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
            var deltaX = centers[triangle].x - centerX;
            var deltaY = centers[triangle].y - centerY;
            var deltaZ = centers[triangle].z - centerZ;
            var distance = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
            if (priority < bestPriority || distance < bestDistance || (distance == bestDistance && triangle < best)) {
                best = triangle;
                bestSlot = i;
                bestPriority = priority;
                bestDistance = distance;
            }
        }
        if (bestSlot >= 0) {
            candidates[bestSlot] = candidates[--count];
        }
        return best;
    }

    private static int SelectSeed(
        ReadOnlySpan<int> candidates, ReadOnlySpan<uint> indices, float3[] centers,
        int[] liveTriangles, bool[] emitted, float3 corner)
    {
        var best = -1;
        var bestLive = long.MaxValue;
        var bestDistance = double.PositiveInfinity;
        foreach (var triangle in candidates) {
            if (emitted[triangle]) {
                continue;
            }
            var a = indices[triangle * 3];
            var b = indices[triangle * 3 + 1];
            var c = indices[triangle * 3 + 2];
            var live = (long)liveTriangles[a] + (b != a ? liveTriangles[b] : 0)
                + (c != a && c != b ? liveTriangles[c] : 0);
            var x = (double)centers[triangle].x - corner.x;
            var y = (double)centers[triangle].y - corner.y;
            var z = (double)centers[triangle].z - corner.z;
            var distance = x * x + y * y + z * z;
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
