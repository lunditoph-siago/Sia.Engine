using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;
using Request = Sia.Engine.Rendering.Pbr.PbrStreamResidency.ChildRequest;
using Page = Sia.Engine.Rendering.Pbr.PbrStreamResidency.BudgetPage;
using Counts = Sia.Engine.Rendering.Pbr.PbrStreamResidency.BudgetCounts;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrGeometryBudgetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CachedPlanningMatches151AdmissionAndStatisticsAcrossChangingFrames(bool shared)
    {
        var random = new Random(50921);
        var trees = new[] { RandomTree(random, 0), RandomTree(random, 1) };
        var canonical = Enumerable.Range(0, 12).ToArray();
        if (shared) { canonical[9] = 8; canonical[11] = 10; }
        var pages = Enumerable.Range(0, 12).Select(i => new PbrSceneStream.PageInfo(i.ToString(), 1,
            random.Next(1, 7), random.Next(1, 9), 1)).ToArray();
        Page[] roots = [new(shared ? -1 : 0, 0, 0), new(shared ? -1 : 1, 1, 0)];
        var rootV = (ulong)(pages[0].Vertices + pages[1].Vertices + 3);
        var rootT = (ulong)(pages[0].Triangles + pages[1].Triangles + 2);
        var layout = new PbrStreamResidency.BudgetWorkspace(trees, canonical, pages, shared);
        for (var frame = 1; frame <= 400; frame++) {
            var requests = new Dictionary<Request, float>();
            var order = Enumerable.Range(0, 16).OrderBy(_ => random.Next()).ToArray();
            foreach (var index in order) {
                if (random.Next(4) == 0) continue;
                var asset = index / 8; var node = index % 8 + 1;
                requests.Add(new(shared ? -1 : asset, asset, node), random.Next(1, 5));
            }
            var recovery = requests.Keys.Where(_ => random.Next(5) == 0)
                .Select(key => key with { Node = trees[key.Asset].Nodes[key.Node].Parent }).ToHashSet();
            var vertexCapacity = checked((uint)rootV + (uint)random.Next(61));
            var triangleCapacity = checked((uint)rootT + (uint)random.Next(61));
            var expected = Reference(trees, canonical, pages, shared, requests, roots, rootV, rootT,
                vertexCapacity, triangleCapacity, recovery);
            var actual = layout.Plan(requests, roots, rootV, rootT, vertexCapacity, triangleCapacity, frame, recovery);
            Assert.Equal(expected.Pages, layout.Result.ToArray());
            Assert.Equal(expected.Counts, actual);
        }
    }

    [Fact]
    public void CompleteObservedSiblingClosurePublishesAtomicallyAtBothExactCaps()
    {
        var tree = BinaryTree(); var pages = UnitPages(5);
        var layout = new PbrStreamResidency.BudgetWorkspace([tree], [0, 1, 2, 3, 4], pages, true);
        var requests = new Dictionary<Request, float> { [new(-1, 0, 3)] = 10, [new(-1, 0, 4)] = 10 };
        Page[] roots = [new(-1, 0, 0)];
        Assert.Equal(new Counts(1, 1, 0, 0, 0), layout.Plan(requests, roots, 1, 1, 5, 5, 1));
        Assert.Equal(roots, layout.Result.ToArray());
        requests.Add(new(-1, 0, 1), 1); requests.Add(new(-1, 0, 2), 1);
        var denied = layout.Plan(requests, roots, 1, 1, 4, 4, 2);
        Assert.Equal(1, denied.Denied); Assert.Equal(1, denied.VertexDenied); Assert.Equal(1, denied.TriangleDenied);
        Assert.Equal(new[] { 0, 1, 2 }, layout.Result.ToArray().Select(page => page.Page));
        Assert.All(layout.Result.ToArray().Skip(1), page => Assert.Equal(1, page.Priority));
        var admitted = layout.Plan(requests, roots, 1, 1, 5, 5, 3);
        Assert.Equal(0, admitted.Denied);
        Assert.Equal(new[] { 0, 3, 4, 1, 2 }, layout.Result.ToArray().Select(page => page.Page));
        Assert.All(layout.Result.ToArray().Skip(1), page => Assert.Equal(10, page.Priority));
        Assert.Throws<InvalidOperationException>(() => layout.Plan(requests, roots, 6, 1, 5, 5, 4));
    }

    [Fact]
    public void StablePageShapeAndPriorityOrderReuseScratchWithoutAllocations()
    {
        var tree = SiblingTree(); var pages = UnitPages(4);
        var layout = new PbrStreamResidency.BudgetWorkspace([tree], [0, 1, 2, 3], pages, true);
        var requests = new Dictionary<Request, float> { [new(-1, 0, 1)] = 1, [new(-1, 0, 2)] = 1, [new(-1, 0, 3)] = 1 };
        Page[] roots = [new(-1, 0, 0)];
        var first = layout.Plan(requests, roots, 1, 1, 8, 8, 1);
        Assert.Equal(5, first.PageVisits); // Includes cross-child duplicate page occurrences.
        Assert.Equal(3, layout.CachedPageVisits);
        var sorts = layout.Sorts; var caches = layout.PageCacheBuilds;
        for (var frame = 2; frame < 32; frame++) layout.Plan(requests, roots, 1, 1, 8, 8, frame);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 32; frame < 288; frame++) {
            requests[new(-1, 0, 1)] = frame;
            layout.Plan(requests, roots, 1, 1, 8, 8, frame);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(sorts, layout.Sorts); Assert.Equal(caches, layout.PageCacheBuilds);
        Assert.All(layout.Result.ToArray().Skip(1), page => Assert.Equal(287, page.Priority));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RequestSignaturePreservesExactFloatAndMembershipComparison(bool shared)
    {
        var layout = new PbrStreamResidency.BudgetWorkspace([BinaryTree()], [0, 1, 2, 3, 4], UnitPages(5), shared);
        var requests = new Dictionary<Request, float> { [new(shared ? -1 : 0, 0, 1)] = 2 };
        var previous = new Dictionary<Request, float>(requests);
        layout.Plan(requests, [new(shared ? -1 : 0, 0, 0)], 1, 1, 5, 5, 1);
        Assert.True(layout.SameRequests(requests, previous));
        var key = requests.Keys.Single();
        requests[key] = MathF.BitIncrement(2);
        Assert.False(layout.SameRequests(requests, previous));
        requests.Clear(); requests.Add(key with { Node = 2 }, 2);
        Assert.False(layout.SameRequests(requests, previous));
        requests.Clear(); Assert.False(layout.SameRequests(requests, previous));
    }

    [Fact]
    public void SortedGroupCacheReordersOnlyWhenPriorityOrRecoveryOrderChanges()
    {
        var tree = BinaryTree(); var pages = UnitPages(5); int[] canonical = [0, 1, 2, 3, 4];
        var layout = new PbrStreamResidency.BudgetWorkspace([tree], canonical, pages, true);
        var requests = new Dictionary<Request, float> { [new(-1, 0, 1)] = 4, [new(-1, 0, 2)] = 4,
            [new(-1, 0, 3)] = 8, [new(-1, 0, 4)] = 8 };
        Page[] roots = [new(-1, 0, 0)]; var recovery = new HashSet<Request>();
        for (var frame = 1; frame <= 4; frame++) {
            if (frame == 2) foreach (var key in requests.Keys.ToArray()) requests[key] *= 2;
            if (frame == 3) { requests[new(-1, 0, 1)] = 20; requests[new(-1, 0, 2)] = 20; }
            if (frame == 4) recovery.Add(new(-1, 0, 1));
            var expected = Reference([tree], canonical, pages, true, requests, roots, 1, 1, 5, 5, recovery);
            Assert.Equal(expected.Counts, layout.Plan(requests, roots, 1, 1, 5, 5, frame, recovery));
            Assert.Equal(expected.Pages, layout.Result.ToArray());
            Assert.Equal(frame <= 2 ? 1 : frame - 1, layout.Sorts);
            Assert.Equal(2, layout.PageCacheBuilds);
        }
    }

    // Test-only 151 reference: dictionary membership, per-child page iteration,
    // complete ancestor closure and original strict comparison/order/counters.
    // It reconstructs scratch per call for isolation; it is not a timing baseline.
    private sealed class ReferenceGroup(Request key)
    {
        internal readonly Request Key = key;
        internal readonly List<Request> Children = [];
        internal float Priority;
        internal bool Recovery;
    }

    private static (Page[] Pages, Counts Counts) Reference(PbrSceneStream.HierarchyInfo[] trees, int[] canonical,
        PbrSceneStream.PageInfo[] pages, bool shared, Dictionary<Request, float> requests, Page[] roots,
        ulong rootV, ulong rootT, uint capacityV, uint capacityT, HashSet<Request> recovery)
    {
        var layout = new PbrGeometryAdmissionLayout(trees, canonical);
        var groups = new Dictionary<Request, ReferenceGroup>();
        foreach (var pair in requests) {
            var key = pair.Key with { Node = layout.ParentLocal(pair.Key.Asset, pair.Key.Node) };
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new(key) { Recovery = recovery.Contains(key) });
            group.Children.Add(pair.Key); group.Priority = System.Math.Max(group.Priority, pair.Value);
        }
        var order = groups.Values.OrderByDescending(group => group.Recovery).ThenByDescending(group => group.Priority)
            .ThenBy(group => group.Key.Instance).ThenBy(group => group.Key.Asset).ThenBy(group => group.Key.Node).ToArray();
        var next = new Dictionary<(int Instance, int Page), float>();
        foreach (var root in roots) next.Add((root.Instance, root.Page), 0);
        var selected = new HashSet<Request>(); var closure = new List<ReferenceGroup>();
        var extra = new List<(int Instance, int Page)>(); var marks = new HashSet<(int Instance, int Page)>();
        var vertices = rootV; var triangles = rootT;
        var denied = 0; var missing = 0; var vertexDenied = 0; var triangleDenied = 0; long visits = 0;
        foreach (var group in order) {
            if (selected.Contains(group.Key)) continue;
            closure.Clear(); var ancestor = group; var complete = true;
            while (!selected.Contains(ancestor.Key)) {
                closure.Add(ancestor); var parent = layout.ParentLocal(ancestor.Key.Asset, ancestor.Key.Node);
                if (parent < 0) break;
                if (!requests.ContainsKey(ancestor.Key) || !groups.TryGetValue(ancestor.Key with { Node = parent }, out var dependency)) { complete = false; break; }
                ancestor = dependency;
            }
            if (!complete) { denied++; missing++; continue; }
            extra.Clear(); marks.Clear(); ulong v = 0, t = 0;
            foreach (var dependency in closure) {
                foreach (var child in dependency.Children) {
                    foreach (var page in layout.Pages(child.Asset, child.Node)) {
                        visits++; var key = (shared ? -1 : child.Instance, page);
                        if (next.ContainsKey(key) || !marks.Add(key)) continue;
                        extra.Add(key); v = checked(v + (ulong)pages[page].Vertices); t = checked(t + (ulong)pages[page].Triangles);
                        if (v > capacityV - vertices || t > capacityT - triangles) goto collected;
                    }
                }
            }
            collected:
            if (v > capacityV - vertices || t > capacityT - triangles) {
                denied++; if (v > capacityV - vertices) vertexDenied++; if (t > capacityT - triangles) triangleDenied++;
                continue;
            }
            vertices += v; triangles += t;
            foreach (var page in extra) next.Add(page, group.Priority);
            foreach (var dependency in closure) selected.Add(dependency.Key);
        }
        return (next.Select(pair => new Page(pair.Key.Instance, pair.Key.Page, pair.Value)).ToArray(),
            new(denied, missing, vertexDenied, triangleDenied, visits));
    }

    private static PbrSceneStream.PageInfo[] UnitPages(int count)
        => Enumerable.Range(0, count).Select(i => new PbrSceneStream.PageInfo(i.ToString(), 1, 1, 1, 1)).ToArray();

    private static PbrSceneStream.HierarchyInfo RandomTree(Random random, int asset)
    {
        var bounds = new Aabb(new float3(-1), new float3(1));
        var nodes = new MeshVirtualPageNode[9]; var parts = new List<PbrSceneStream.PagePart>();
        for (var node = 0; node < nodes.Length; node++) {
            var first = parts.Count; var count = node == 0 ? 1 : random.Next(1, 4);
            for (var i = 0; i < count; i++) parts.Add(new(node == 0 ? asset : random.Next(12), 0, 1));
            var child = node * 2 + 1; var children = System.Math.Clamp(nodes.Length - child, 0, 2);
            nodes[node] = new(bounds, 1, node == 0 ? -1 : (node - 1) / 2, child, children, first, count, count);
        }
        return new(1, nodes, parts.ToArray());
    }

    private static PbrSceneStream.HierarchyInfo BinaryTree()
    {
        var b = new Aabb(new float3(-1), new float3(1));
        return new(1, [new(b, 1, -1, 1, 2, 0, 1, 1), new(b, 1, 0, 3, 2, 1, 1, 1),
            new(b, 0, 0, 5, 0, 2, 1, 1), new(b, 0, 1, 5, 0, 3, 1, 1), new(b, 0, 1, 5, 0, 4, 1, 1)],
            [new(0, 0, 1), new(1, 0, 1), new(2, 0, 1), new(3, 0, 1), new(4, 0, 1)]);
    }

    private static PbrSceneStream.HierarchyInfo SiblingTree()
    {
        var b = new Aabb(new float3(-1), new float3(1));
        return new(1, [new(b, 1, -1, 1, 3, 0, 1, 1), new(b, 0, 0, 4, 0, 1, 2, 2),
            new(b, 0, 0, 4, 0, 3, 2, 2), new(b, 0, 0, 4, 0, 5, 1, 1)],
            [new(0, 0, 1), new(1, 0, 1), new(2, 0, 1), new(1, 0, 1), new(3, 0, 1), new(3, 0, 1)]);
    }
}
