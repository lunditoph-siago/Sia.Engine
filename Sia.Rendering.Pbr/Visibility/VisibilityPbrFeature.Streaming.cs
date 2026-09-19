using System.Buffers;
using Sia.Asset;
using Sia.Engine.Camera;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrStreamingStatistics(long GeometryBufferBytes, long DetailResidentBytes,
    long UploadedBytes, int PeakUploadBytesPerFrame, int ResidentDetails, int PublishedDetails, int EvictedDetails, bool Loading);

public sealed partial class VisibilityPbrFeature
{
    private readonly record struct GeometryReservation(int Vertices, int Indices, int Triangles);
    private readonly record struct PageAllocation(GeometryRangeAllocator.Range Vertices, GeometryRangeAllocator.Range Indices, GeometryRangeAllocator.Range Triangles)
    { public long Bytes => Vertices.Length * 48L + Indices.Length * 4L + Triangles.Length * 8L; }
    private sealed record StreamAsset(int[] Instances, int[] ClusterOffsets, int ClusterCapacity, FixedClusterGpu[] RootClusters,
        Aabb Bounds, PbrSceneStream.Detail? Detail)
    {
        public PageAllocation? Resident;
        public float Priority;
        public bool Failed;
        public Aabb ChangedBounds = Bounds;
    }
    private sealed class GeometryStreaming
    {
        public required PbrSceneStream Source;
        public required GeometryReservation Reservation;
        public required StreamAsset[] Assets;
        public required GeometryRangeAllocator Vertices, Indices, Triangles;
        public required int UploadBudget;
        public CancellationTokenSource Cancellation = new();
        public Task<(AssetChunkLease Lease, PbrGeometryPage Page)>? Pending;
        public int PendingAsset;
        public (AssetChunkLease Lease, PbrGeometryPage Page)? Ready;
        public PageAllocation? Upload;
        public int Cursor;
        public long UploadedBytes;
        public int PeakUploadBytes;
        public int Published, Evicted;
        public int Frames;
        public ulong? PreparedFrame;
        public float AdmissionLimit = float.PositiveInfinity;
        public float4x4 LastProjection;
    }
    private GeometryStreaming? _streaming;
    public PbrStreamingStatistics? StreamingStatistics => _streaming is not { } s ? null : new(
        s.Reservation.Vertices * 48L + s.Reservation.Indices * 4L + s.Reservation.Triangles * 8L,
        s.Assets.Sum(a => a.Resident?.Bytes ?? 0) + (s.Upload?.Bytes ?? 0), s.UploadedBytes,
        s.PeakUploadBytes, s.Assets.Count(a => a.Resident is not null), s.Published, s.Evicted, s.Pending is not null || s.Ready is not null);

    /// <summary>Creates resident coarse geometry and a bounded detail arena. Call StopStreamingAsync before disposing its resource world.</summary>
    public static VisibilityPbrFeature CreateStreamScene(in GpuFrame frame, PbrSceneStream source,
        WGPUTextureFormat outputFormat, long detailByteBudget = 64 * 1024 * 1024,
        int uploadBytesPerFrame = 1024 * 1024, VisibilityDebugMode mode = VisibilityDebugMode.Shaded)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (detailByteBudget is < 1024 * 1024 or > 256 * 1024 * 1024 || uploadBytesPerFrame < 65536)
            throw new ArgumentOutOfRangeException(nameof(detailByteBudget));
        var bootstrap = source.Bootstrap;
        var materialIds = source.Instances.ToArray().Select(i => i.MaterialIndex).Distinct().Order().ToArray();
        var materialMap = materialIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        var instances = source.Instances.ToArray().Select(i => i with { MaterialIndex = materialMap[i.MaterialIndex] }).ToArray();
        var pages = source.RootPages;
        if (pages.Length == 0) throw new InvalidOperationException("Startup pages have already been consumed by a renderer.");
        var localBounds = pages.Select(PageBounds).ToArray();
        var rootV = pages.Sum(p => p.Size.Vertices); var rootI = pages.Sum(p => p.Size.Indices); var rootT = pages.Sum(p => p.Size.Triangles);
        // Three independently bounded arenas prevent one buffer's fragmentation from corrupting another.
        var fineV = checked((int)(detailByteBudget * 6 / 10 / 48));
        var fineI = checked((int)(detailByteBudget * 2 / 10 / 4));
        var fineT = checked((int)(detailByteBudget * 2 / 10 / 8));
        var reservation = new GeometryReservation(System.Math.Max(1, rootV + fineV), System.Math.Max(1, rootI + fineI), System.Math.Max(1, rootT + fineT));
        var assets = new StreamAsset[pages.Length]; var clusterCount = 0; uint triangleCapacity = 0;
        for (var i = 0; i < assets.Length; i++) {
            var owners = instances.Select((v, n) => (v, n)).Where(x => x.v.AssetIndex == i).Select(x => x.n).ToArray();
            var size = pages[i].Size; var detail = source.Details[i];
            if (detail is not null && (detail.Size.Vertices > fineV || detail.Size.Indices > fineI || detail.Size.Triangles > fineT))
                throw new ArgumentException("The detail arena cannot hold the largest geometry page.", nameof(detailByteBudget));
            var count = System.Math.Max(size.Clusters, detail?.Size.Clusters ?? 0);
            if ((long)count * owners.Length * 80 > uploadBytesPerFrame)
                throw new ArgumentException("Atomic cluster publication exceeds the per-frame upload budget.", nameof(uploadBytesPerFrame));
            var offsets = new int[owners.Length];
            for (var j = 0; j < owners.Length; j++) { offsets[j] = clusterCount; clusterCount = checked(clusterCount + count); }
            triangleCapacity = checked(triangleCapacity + (uint)owners.Length * (uint)System.Math.Max(size.Triangles, detail?.Size.Triangles ?? 0));
            var bounds = new Aabb(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
            Aabb? worldBounds = null;
            foreach (var owner in owners) IncludeBounds(ref worldBounds, localBounds[i], instances[owner].Transform);
            assets[i] = new(owners, offsets, count, new FixedClusterGpu[count], worldBounds ?? bounds, detail);
        }
        if (clusterCount == 0) throw new ArgumentException("Streaming requires opaque geometry.", nameof(source));
        var empty = MeshletRasterData.Combine([]);
        var scene = new SceneLodData(empty, [], instances.Select(i => new uint4(0, 0, 0, (uint)i.AssetIndex)).ToArray(), default, 1,
            triangleCapacity, localBounds.Select(b => (Aabb?)b).ToArray());
        var feature = Create(in frame, empty, instances, null, outputFormat, mode, null, default,
            scene: scene, materials: materialIds.Select(i => bootstrap.Materials.Span[i]).ToArray(), fixedClusters: new FixedClusterGpu[clusterCount], reservation: reservation);
        var streaming = new GeometryStreaming { Source = source, Reservation = reservation, Assets = assets,
            Vertices = new(rootV, fineV), Indices = new(rootI, fineI), Triangles = new(rootT, fineT), UploadBudget = uploadBytesPerFrame };
        feature._streaming = streaming;
        int v = 0, indexOffset = 0, t = 0;
        for (var i = 0; i < pages.Length; i++) if (pages[i] is { } page) {
            var allocation = new PageAllocation(new(v, page.Size.Vertices), new(indexOffset, page.Size.Indices), new(t, page.Size.Triangles));
            var cursor = 0;
            feature.UploadPage(page, allocation, ref cursor, int.MaxValue);
            var clusters = feature.RelocateClusters(page, allocation, assets[i].ClusterCapacity);
            clusters.CopyTo(assets[i].RootClusters, 0);
            feature.PublishClusters(assets[i], clusters);
            v += page.Size.Vertices; indexOffset += page.Size.Indices; t += page.Size.Triangles;
        }
        source.ReleaseRootPages();
        return feature;
    }

    internal static Aabb PageBounds(PbrGeometryPage page)
    {
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var cluster in page.Clusters) { minimum = math.min(minimum, new float3(cluster.X, cluster.Y, cluster.Z)); maximum = math.max(maximum, cluster.Maximum.xyz); }
        return new(minimum, maximum);
    }

    private void UpdateStreaming(CameraMatrices camera, ulong frameIndex)
    {
        if (_streaming is not { } s) return;
        if (s.PreparedFrame == frameIndex) return;
        s.PreparedFrame = frameIndex;
        var before = s.UploadedBytes;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { UpdateStreamingCore(camera, s); }
        finally {
            s.PeakUploadBytes = System.Math.Max(s.PeakUploadBytes, checked((int)(s.UploadedBytes - before)));
            _frameStatistics = _frameStatistics with { UploadBytes = s.UploadedBytes - before,
                StreamingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        }
    }

    private void UpdateStreamingCore(CameraMatrices camera, GeometryStreaming s)
    {
        if (s.Frames++ % 12 == 0 && !s.LastProjection.Equals(camera.ViewProj)) {
            s.LastProjection = camera.ViewProj; s.AdmissionLimit = float.PositiveInfinity; foreach (var asset in s.Assets) {
            var center = (asset.Bounds.Min + asset.Bounds.Max) * .5f;
            var radius = math.length(asset.Bounds.Max - asset.Bounds.Min) * .5f;
            var projected = math.mul(camera.ViewProj, new float4(center, 1));
            var distance = MathF.Max(.1f, math.length(center - camera.WorldPosition) - radius);
            // Coarse geometry remains for shadows and out-of-view content; prioritize near visible detail.
            var visible = projected.w > 0 && MathF.Abs(projected.x) <= projected.w + radius * 2 && MathF.Abs(projected.y) <= projected.w + radius * 2;
            asset.Priority = visible ? distance : float.PositiveInfinity;
        }}
        if (s.Pending is { IsCompleted: true } pending) {
            s.Pending = null;
            try { s.Ready = pending.GetAwaiter().GetResult(); }
            catch (Exception error) { s.Assets[s.PendingAsset].Failed = true; Console.Error.WriteLine($"Geometry page kept coarse: {error.Message}"); }
        }
        if (s.Ready is { } ready) {
            var budget = s.UploadBudget;
            var asset = s.Assets[s.PendingAsset];
            if (!float.IsFinite(asset.Priority)) { ready.Lease.Dispose(); s.Ready = null; if (s.Upload is { } stale) Release(stale); s.Upload = null; }
            else {
                var deferred = false;
                s.Upload ??= AllocatePage(ready.Page.Size, asset.Priority, ref budget, out deferred);
                if (s.Upload is { } allocation) {
                    var bytes = UploadPage(ready.Page, allocation, ref s.Cursor, budget);
                    s.UploadedBytes += bytes;
                    var publicationBytes = asset.ClusterCapacity * asset.Instances.Length * 80;
                    if (s.Cursor == ready.Page.Size.Vertices * 48 + ready.Page.Size.Indices * 4 + ready.Page.Size.Triangles * 8
                        && budget - bytes >= publicationBytes) {
                        PublishClusters(asset, RelocateClusters(ready.Page, allocation, asset.ClusterCapacity));
                        s.UploadedBytes += publicationBytes;
                        asset.Resident = allocation; s.Published++; s.Upload = null; s.Cursor = 0;
                        ready.Lease.Dispose(); s.Ready = null;
                    }
                } else if (!deferred) { s.AdmissionLimit = asset.Priority; ready.Lease.Dispose(); s.Ready = null; }
            }
        }
        if (s.Ready is not null || s.Pending is not null) return;
        var worst = s.Assets.Where(a => a.Resident is not null).Select(a => a.Priority).DefaultIfEmpty(float.PositiveInfinity).Max();
        var candidate = -1; var best = s.AdmissionLimit;
        for (var id = 0; id < s.Assets.Length; id++) {
            var asset = s.Assets[id];
            if (asset.Detail is null || asset.Resident is not null || asset.Failed || asset.Priority >= best
                || (s.Evicted != 0 && asset.Priority >= worst * .9f)) continue;
            candidate = id; best = asset.Priority;
        }
        if (candidate < 0) return;
        s.PendingAsset = candidate; s.Cursor = 0;
        // HTTP/hash/decode must never inherit the native browser graphics owner.
        // Its synchronization context may not make managed JavaScript imports.
        s.Pending = Task.Run(() => ReadPageAsync(s.Source, candidate, s.Cancellation.Token), s.Cancellation.Token);
    }

    private static async Task<(AssetChunkLease, PbrGeometryPage)> ReadPageAsync(PbrSceneStream source, int id, CancellationToken token)
    {
        var lease = await source.AcquireAsync(id, token).ConfigureAwait(false);
        try {
            var page = await Task.Run(() => PbrGeometryPage.Decode(PbrStreamBlock.Decode(lease.Memory.Span)), token).ConfigureAwait(false);
            if (page.Size != source.Details[id]!.Size) throw new InvalidDataException("Detail page differs from its reservation.");
            return (lease, page);
        } catch { lease.Dispose(); throw; }
    }

    private PageAllocation? AllocatePage(PbrGeometryPage.Counts size, float priority, ref int budget, out bool deferred)
    {
        deferred = false;
        var s = _streaming!;
        while (true) {
            var v = s.Vertices.Allocate(size.Vertices); var i = s.Indices.Allocate(size.Indices); var t = s.Triangles.Allocate(size.Triangles);
            if (v is { } vertices && i is { } indices && t is { } triangles) return new(vertices, indices, triangles);
            if (v is { } rv) s.Vertices.Release(rv); if (i is { } ri) s.Indices.Release(ri); if (t is { } rt) s.Triangles.Release(rt);
            var victim = s.Assets.Where(a => a.Resident is not null && a.Priority > priority * 1.1f).MaxBy(a => a.Priority);
            if (victim is null) return null;
            var bytes = victim.ClusterCapacity * victim.Instances.Length * 80;
            if (bytes > budget) { deferred = true; return null; }
            PublishClusters(victim, victim.RootClusters); Release(victim.Resident!.Value); victim.Resident = null; s.Evicted++;
            budget -= bytes; s.UploadedBytes += bytes;
        }
    }
    private void Release(PageAllocation allocation)
    { var s = _streaming!; s.Vertices.Release(allocation.Vertices); s.Indices.Release(allocation.Indices); s.Triangles.Release(allocation.Triangles); }

    private FixedClusterGpu[] RelocateClusters(PbrGeometryPage page, PageAllocation allocation, int capacity)
    {
        var clusters = new FixedClusterGpu[capacity];
        for (var i = 0; i < page.Size.Clusters; i++) {
            var c = page.Clusters[i];
            clusters[i] = new(c.X, c.Y, c.Z, c.VertexOffset + (uint)allocation.Indices.Offset, c.Maximum, c.Sphere, c.Cone,
                new(c.Work.x + (uint)allocation.Triangles.Offset, 0, c.Work.z, c.Work.w + (uint)allocation.Indices.Offset));
        }
        return clusters;
    }
    private void PublishClusters(StreamAsset asset, FixedClusterGpu[] clusters)
    {
        var queue = _queue.GetWgpu<WGPUQueue>();
        for (var owner = 0; owner < asset.Instances.Length; owner++) {
            for (var i = 0; i < clusters.Length; i++) clusters[i] = clusters[i] with { Work = clusters[i].Work with { y = (uint)asset.Instances[owner] } };
            Wgpu.WriteBuffer<FixedClusterGpu>(queue, _fixedGeometry!.Value.Source.GetWgpu<WGPUBuffer>(), (ulong)asset.ClusterOffsets[owner] * 80, clusters);
        }
        Aabb? local = null;
        foreach (var cluster in clusters) {
            if (cluster.Work.z == 0) continue;
            var box = new Aabb(new(cluster.MinimumX, cluster.MinimumY, cluster.MinimumZ), cluster.Maximum.xyz);
            local = local is { } previous ? Aabb.Union(previous, box) : box;
        }
        Aabb? changed = asset.ChangedBounds;
        foreach (var instance in asset.Instances) IncludeBounds(ref changed, local, _transforms[instance]);
        asset.ChangedBounds = changed!.Value;
        _sceneChanges.Add(changed);
        _frameStatistics = _frameStatistics with { Publications = _frameStatistics.Publications + 1 };
    }

    private int UploadPage(PbrGeometryPage page, PageAllocation allocation, ref int cursor, int budget)
    {
        var queue = _queue.GetWgpu<WGPUQueue>(); var capacity = _streaming!.Reservation.Vertices;
        var verticesEnd = page.Size.Vertices * 48; var indicesEnd = verticesEnd + page.Size.Indices * 4;
        var end = indicesEnd + page.Size.Triangles * 8; var started = cursor;
        // Slice/cast once per upload call, rather than per element on the WASM path.
        var indices = page.Indices; var triangles = page.Triangles;
        var referenceCount = indices.Length - triangles.Length;
        var staging = ArrayPool<uint>.Shared.Rent(16384);
        try { while (cursor < end && budget >= 16) {
            if (cursor < verticesEnd) {
                var planeBytes = page.Size.Vertices * 16; var plane = cursor / planeBytes; var offset = cursor % planeBytes;
                var count = System.Math.Min(planeBytes - offset, budget / 16 * 16);
                Wgpu.WriteBuffer<byte>(queue, _geometry[0].GetWgpu<WGPUBuffer>(), (ulong)(plane * (long)capacity + allocation.Vertices.Offset) * 16 + (ulong)offset,
                    page.Bytes.Span.Slice(24 + cursor, count));
                cursor += count; budget -= count;
            } else if (cursor < indicesEnd) {
                var offset = (cursor - verticesEnd) / 4; var count = System.Math.Min(System.Math.Min(page.Size.Indices - offset, budget / 4), 16384);
                for (var j = 0; j < count; j++) staging[j] = indices[offset + j] + (offset + j < referenceCount ? (uint)allocation.Vertices.Offset : 0);
                Wgpu.WriteBuffer<uint>(queue, _geometry[1].GetWgpu<WGPUBuffer>(), (ulong)(allocation.Indices.Offset + offset) * 4, staging.AsSpan(0, count));
                cursor += count * 4; budget -= count * 4;
            } else {
                var offset = (cursor - indicesEnd) / 8; var count = System.Math.Min(System.Math.Min(page.Size.Triangles - offset, budget / 8), 8192);
                for (var j = 0; j < count; j++) { var triangle = triangles[offset + j]; staging[j * 2] = triangle.x + (uint)allocation.Indices.Offset; staging[j * 2 + 1] = triangle.y; }
                Wgpu.WriteBuffer<uint>(queue, _geometry[2].GetWgpu<WGPUBuffer>(), (ulong)(allocation.Triangles.Offset + offset) * 8, staging.AsSpan(0, count * 2));
                cursor += count * 8; budget -= count * 8;
            }
        }} finally { ArrayPool<uint>.Shared.Return(staging); }
        return cursor - started;
    }

    public async ValueTask StopStreamingAsync()
    {
        if (_streaming is not { } s) return;
        _streaming = null; await s.Cancellation.CancelAsync();
        if (s.Pending is { } pending) { try { var result = await pending; result.Lease.Dispose(); } catch (OperationCanceledException) { } catch (Exception) { /* A stopped page never publishes. */ } }
        s.Ready?.Lease.Dispose(); s.Cancellation.Dispose();
    }
}
