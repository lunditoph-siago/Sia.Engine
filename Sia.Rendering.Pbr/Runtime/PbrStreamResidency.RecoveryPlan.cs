namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    private readonly record struct RecoveryPage(Key Key, StreamPageAllocation Allocation, bool Desired);
    private readonly record struct RecoveryWindow(ulong Start, ulong End)
    {
        public ulong Length => End - Start;
    }
    private readonly record struct RecoveryScan(bool Vertices, RecoveryWindow Window);
    private sealed record RecoveryProof(Key[] Victims, ulong RestoreBytes, ulong WindowBytes);

    // This only proves a layout. The owner must revalidate it, retire every
    // affected draw and hold normal reservations before releasing any victim.
    private Key[]? PlanRecovery(Key target)
    {
        if (_stopped || _upload is not null || _relocation is not null || _relocationReservation is not null
            || !RecoveryKeyOwned(target) || _resident.ContainsKey(target) || !IsAdmitted(target)) return null;
        var required = _pages[target.Page];
        if (required.Vertices <= 0 || required.Triangles <= 0) return null;
        var shape = PbrGpuScene.RootShape(_source, SharedInstances);
        if (shape.Vertices > _arena.VertexCapacity || shape.Triangles > _arena.TriangleCapacity) return null;
        var rootsV = new List<StreamRange>();
        var rootsT = new List<StreamRange>();
        var pages = new List<RecoveryPage>();
        ulong rootVertices = 0, rootTriangles = 0, detailVertices = 0, detailTriangles = 0;
        foreach (var pair in _resident) {
            if (!RecoveryKeyOwned(pair.Key)) return null;
            var page = _pages[pair.Key.Page];
            var allocation = pair.Value.Allocation;
            if (page.Vertices <= 0 || page.Triangles <= 0
                || allocation.Vertices.Count != (uint)page.Vertices || allocation.Triangles.Count != (uint)page.Triangles
                || !RecoveryRangeValid(allocation.Vertices, _arena.VertexCapacity)
                || !RecoveryRangeValid(allocation.Triangles, _arena.TriangleCapacity)) return null;
            if (pair.Value.Root) {
                rootsV.Add(allocation.Vertices);
                rootsT.Add(allocation.Triangles);
                rootVertices += allocation.Vertices.Count;
                rootTriangles += allocation.Triangles.Count;
                if (rootVertices > shape.Vertices || rootTriangles > shape.Triangles) return null;
            }
            else {
                if (allocation.Vertices.Offset < shape.Vertices || allocation.Triangles.Offset < shape.Triangles) return null;
                pages.Add(new(pair.Key, allocation, IsAdmitted(pair.Key)));
                detailVertices += allocation.Vertices.Count;
                detailTriangles += allocation.Triangles.Count;
                if (detailVertices > _arena.VertexCapacity || detailTriangles > _arena.TriangleCapacity) return null;
            }
        }
        // Transparent bootstrap precedes the root copies. Both stay packed and
        // pinned; the whole-tail fallback is valid only under this invariant.
        if (!RecoveryPinnedPrefix(rootsV, shape.Vertices - rootVertices, shape.Vertices)
            || !RecoveryPinnedPrefix(rootsT, shape.Triangles - rootTriangles, shape.Triangles)) return null;
        var expectedBytes = (shape.Vertices + detailVertices) * StreamGeometryPage.VertexBytes
            + (shape.Triangles + detailTriangles) * 12;
        if (expectedBytes != _arena.UsedBytes || pages.Count == 0) return null;
        var byV = Enumerable.Range(0, pages.Count).ToArray();
        var byT = byV.ToArray();
        Array.Sort(byV, (left, right) => pages[left].Allocation.Vertices.Offset.CompareTo(pages[right].Allocation.Vertices.Offset));
        Array.Sort(byT, (left, right) => pages[left].Allocation.Triangles.Offset.CompareTo(pages[right].Allocation.Triangles.Offset));
        if (!RecoveryDisjoint(pages, byV, true, shape.Vertices)
            || !RecoveryDisjoint(pages, byT, false, shape.Triangles)) return null;
        var windowsV = RecoveryWindows(pages, byV, true, shape.Vertices, _arena.VertexCapacity, (uint)required.Vertices);
        var windowsT = RecoveryWindows(pages, byT, false, shape.Triangles, _arena.TriangleCapacity, (uint)required.Triangles);
        RecoveryProof? best = null;
        foreach (var windowV in windowsV)
            foreach (var windowT in windowsT) {
                var proof = RecoveryClosure(pages, byV, byT, windowV, windowT, (uint)required.Vertices, (uint)required.Triangles);
                if (proof is not null && (best is null || CompareRecoveryProof(proof, best) < 0)) best = proof;
            }
        if (best is not null) return best.Victims;
        // At most four local window pairs were tried. The complete nonroot
        // tail supplies a proof whenever the remaining desired set fits it.
        return RecoveryClosure(pages, byV, byT,
            new(shape.Vertices, _arena.VertexCapacity), new(shape.Triangles, _arena.TriangleCapacity),
            (uint)required.Vertices, (uint)required.Triangles)?.Victims;
    }

    private bool RecoveryKeyOwned(Key key)
        => (uint)key.Page < (uint)_pages.Length && (SharedInstances
            ? key.Instance == -1 : (uint)key.Instance < (uint)_instances.Length);

    private static bool RecoveryRangeValid(StreamRange range, uint capacity)
        => range.Count != 0 && range.Generation != 0 && (ulong)range.Offset + range.Count <= capacity;

    private static bool RecoveryPinnedPrefix(List<StreamRange> ranges, ulong start, ulong end)
    {
        ranges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        foreach (var range in ranges) {
            if (range.Offset != start) return false;
            start += range.Count;
        }
        return start == end;
    }

    private static StreamRange RecoveryRange(RecoveryPage page, bool vertices)
        => vertices ? page.Allocation.Vertices : page.Allocation.Triangles;

    private static bool RecoveryDisjoint(List<RecoveryPage> pages, int[] order, bool vertices, ulong prefix)
    {
        var end = prefix;
        foreach (var index in order) {
            var range = RecoveryRange(pages[index], vertices);
            if (range.Offset < end) return false;
            end = (ulong)range.Offset + range.Count;
        }
        return true;
    }

    private static List<RecoveryWindow> RecoveryWindows(List<RecoveryPage> pages, int[] order, bool vertices,
        ulong prefix, uint capacity, uint required)
    {
        var reclaimable = new List<RecoveryWindow>();
        void Add(ulong start, ulong end) {
            if (start == end) return;
            if (reclaimable.Count != 0 && reclaimable[^1].End == start)
                reclaimable[^1] = new(reclaimable[^1].Start, end);
            else reclaimable.Add(new(start, end));
        }
        var cursor = prefix;
        foreach (var index in order) {
            var page = pages[index];
            var range = RecoveryRange(page, vertices);
            Add(cursor, range.Offset);
            cursor = (ulong)range.Offset + range.Count;
            if (!page.Desired) Add(range.Offset, cursor);
        }
        Add(cursor, capacity);
        var best = new List<RecoveryWindow>(2);
        void Consider(RecoveryWindow window) {
            if (best.Contains(window)) return;
            var at = best.FindIndex(previous => window.Length < previous.Length
                || (window.Length == previous.Length && window.Start < previous.Start));
            if (at < 0) at = best.Count;
            best.Insert(at, window);
            if (best.Count > 2) best.RemoveAt(2);
        }
        var left = 0;
        ulong available = 0;
        for (var right = 0; right < reclaimable.Count; right++) {
            available += reclaimable[right].Length;
            while (left < right && available - reclaimable[left].Length >= required)
                available -= reclaimable[left++].Length;
            if (available < required) continue;
            var excess = available - required;
            // Removing this physical length cannot remove more reclaimable
            // space than the excess. Cross-pool closure can differ by edge.
            Consider(new(reclaimable[left].Start + excess, reclaimable[right].End));
            Consider(new(reclaimable[left].Start, reclaimable[right].End - excess));
        }
        return best;
    }

    private static RecoveryProof? RecoveryClosure(List<RecoveryPage> pages, int[] byV, int[] byT,
        RecoveryWindow windowV, RecoveryWindow windowT, uint targetVertices, uint targetTriangles)
    {
        if (windowV.Start >= windowV.End || windowT.Start >= windowT.End) return null;
        var selected = new bool[pages.Count];
        var scans = new List<RecoveryScan> { new(true, windowV), new(false, windowT) };
        ulong vertices = targetVertices, triangles = targetTriangles;
        var count = 0;
        void Expand(bool vertex, StreamRange range) {
            var old = vertex ? windowV : windowT;
            var start = System.Math.Min(old.Start, range.Offset);
            var end = System.Math.Max(old.End, (ulong)range.Offset + range.Count);
            if (start < old.Start) scans.Add(new(vertex, new(start, old.Start)));
            if (end > old.End) scans.Add(new(vertex, new(old.End, end)));
            if (vertex) windowV = new(start, end); else windowT = new(start, end);
        }
        for (var scanIndex = 0; scanIndex < scans.Count; scanIndex++) {
            var scan = scans[scanIndex];
            var order = scan.Vertices ? byV : byT;
            var low = 0;
            var high = order.Length;
            while (low < high) {
                var middle = low + (high - low) / 2;
                var range = RecoveryRange(pages[order[middle]], scan.Vertices);
                if ((ulong)range.Offset + range.Count <= scan.Window.Start) low = middle + 1;
                else high = middle;
            }
            for (var at = low; at < order.Length; at++) {
                var index = order[at];
                var page = pages[index];
                var range = RecoveryRange(page, scan.Vertices);
                if (range.Offset >= scan.Window.End) break;
                if (selected[index]) continue;
                selected[index] = true;
                count++;
                if (page.Desired) {
                    vertices += page.Allocation.Vertices.Count;
                    triangles += page.Allocation.Triangles.Count;
                }
                Expand(true, page.Allocation.Vertices);
                Expand(false, page.Allocation.Triangles);
            }
        }
        if (count == 0 || vertices > windowV.Length || triangles > windowT.Length) return null;
        var victims = new Key[count];
        var next = 0;
        for (var i = 0; i < pages.Count; i++) {
            var page = pages[i];
            if (selected[i]) victims[next++] = page.Key;
            else if (RecoveryIntersects(page.Allocation.Vertices, windowV)
                || RecoveryIntersects(page.Allocation.Triangles, windowT)) return null;
        }
        Array.Sort(victims, static (left, right) => {
            var order = left.Instance.CompareTo(right.Instance);
            return order != 0 ? order : left.Page.CompareTo(right.Page);
        });
        return new(victims, vertices * StreamGeometryPage.VertexBytes + triangles * 12,
            windowV.Length * StreamGeometryPage.VertexBytes + windowT.Length * 12);
    }

    private static bool RecoveryIntersects(StreamRange range, RecoveryWindow window)
        => range.Offset < window.End && (ulong)range.Offset + range.Count > window.Start;

    private static int CompareRecoveryProof(RecoveryProof left, RecoveryProof right)
    {
        var order = left.Victims.Length.CompareTo(right.Victims.Length);
        if (order != 0) return order;
        order = left.RestoreBytes.CompareTo(right.RestoreBytes);
        if (order != 0) return order;
        order = left.WindowBytes.CompareTo(right.WindowBytes);
        if (order != 0) return order;
        for (var i = 0; i < left.Victims.Length; i++) {
            order = left.Victims[i].Instance.CompareTo(right.Victims[i].Instance);
            if (order != 0) return order;
            order = left.Victims[i].Page.CompareTo(right.Victims[i].Page);
            if (order != 0) return order;
        }
        return 0;
    }
}
