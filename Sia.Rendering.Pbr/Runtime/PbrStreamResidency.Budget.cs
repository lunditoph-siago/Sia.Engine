using System.Diagnostics;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    private readonly record struct ChildRequest(int Instance, int Asset, int Node);

    private sealed class BudgetGroup(ChildRequest key)
    {
        public readonly ChildRequest Key = key;
        public readonly List<ChildRequest> Children = [];
        public long Frame;
        public float Priority;
        public bool Recovery;
        public int Slot;
    }

    private Dictionary<Key, float>? _admitted, _budgetNext;
    private Dictionary<ChildRequest, float> _budgetRequests = [], _budgetPreviousRequests = [];
    private readonly Dictionary<ChildRequest, BudgetGroup> _budgetGroups = [];
    private readonly List<BudgetGroup> _budgetOrder = [], _budgetClosure = [];
    private readonly HashSet<ChildRequest> _budgetSelected = [];
    private readonly HashSet<Key> _budgetGroupPages = [];
    private readonly List<Key> _budgetPageList = [];
    private PbrGeometryAdmissionLayout _budgetLayout = null!;
    private BudgetGroup?[] _budgetSharedGroups = [];
    private PbrAdmissionEpochSet _budgetSelectedNodes = null!, _budgetPlanPages = null!, _budgetGroupPageMarks = null!;
    private Dictionary<ChildRequest, float>? _viewRequests;
    private Key[] _budgetRootKeys = [];
    private ulong _budgetRootVertices, _budgetRootTriangles;
    private uint _budgetVertexCapacity, _budgetTriangleCapacity;
    private bool _budgetEnabled, _budgetHasRootShape, _budgetHasSignature, _budgetPending;
    private int _budgetActiveDenied, _budgetNextDenied;
    private int _budgetMissingAncestors, _budgetVertexDenied, _budgetTriangleDenied;
    private long _budgetRebuilds, _budgetCacheHits, _budgetPageVisits;
    private double _budgetMilliseconds;
    private int _readAheadDemands;
    private readonly HashSet<int> _readAheadPages = [];

    private void InitializeBudgetLayout()
    {
        _budgetLayout = new(_source.Hierarchies, _canonicalPages);
        if (!SharedInstances) return;
        _budgetSharedGroups = new BudgetGroup?[_budgetLayout.NodeCount];
        _budgetSelectedNodes = new(_budgetLayout.NodeCount);
        _budgetPlanPages = new(_pages.Length); _budgetGroupPageMarks = new(_pages.Length);
    }

    private BudgetGroup? FindBudgetGroup(ChildRequest key, bool create = false)
    {
        var slot = _budgetLayout.Node(key.Asset, key.Node);
        if (SharedInstances) {
            var group = _budgetSharedGroups[slot];
            if (group is null && create) _budgetSharedGroups[slot] = group = new(key) { Slot = slot };
            return group;
        }
        if (!_budgetGroups.TryGetValue(key, out var found) && create)
            _budgetGroups.Add(key, found = new(key) { Slot = slot });
        return found;
    }

    private bool BudgetGroupSelected(BudgetGroup group)
        => SharedInstances ? _budgetSelectedNodes.Contains(group.Slot) : _budgetSelected.Contains(group.Key);

    private void AppendBudgetReadAhead()
    {
        _readAheadPages.Clear();
        if (_budgetPending && _budgetNext is { } next && _recovery is not { Started: true }) {
            foreach (var pair in next) {
                if (_resident.ContainsKey(pair.Key) || _missingPages.ContainsKey(pair.Key.Page)) continue;
                var page = _pages[pair.Key.Page];
                _demands.Add(new(page.Id, page.DecodedBytes, pair.Value));
                _readAheadPages.Add(pair.Key.Page);
            }
        }
        _readAheadDemands = _readAheadPages.Count;
    }

    private void EnableBudgetAdmission()
    {
        if (_budgetEnabled) return;
        _budgetEnabled = true;
        foreach (var pair in _resident)
            if (!pair.Value.Root) Changed(pair.Key);
        Revision++;
        _cuts.Clear();
        _groupCuts.Clear();
    }

    private bool IsAdmitted(Key key)
        => !_budgetEnabled || (_admitted?.ContainsKey(key) ?? false)
            || (_resident.TryGetValue(key, out var page) && page.Root);

    private void RequestChild(ChildRequest child, float priority, bool recoveryFresh = true)
    {
        if (recoveryFresh && _recovery is { Started: true }) _recoveryIncoming.Add(child);
        if (!_budgetRequests.TryGetValue(child, out var previous) || priority > previous)
            _budgetRequests[child] = priority;
        if (_viewRequests is { } requests && (!requests.TryGetValue(child, out previous) || priority > previous))
            requests[child] = priority;
    }

    private void BeginBudgetFrame()
    {
        _budgetRequests.Clear();
        if (_budgetPending) {
            var previous = _admitted;
            _admitted = _budgetNext!;
            _budgetNext = previous;
            _budgetPending = false;
            _budgetActiveDenied = _budgetNextDenied;
            var changed = previous is null || previous.Count != _admitted.Count;
            if (!changed)
                foreach (var key in _admitted.Keys)
                    if (!previous!.ContainsKey(key)) { changed = true; break; }
            if (changed) {
                foreach (var pair in _resident) {
                    if (pair.Value.Root) continue;
                    if ((previous?.ContainsKey(pair.Key) ?? false) != _admitted.ContainsKey(pair.Key)) Changed(pair.Key);
                }
                Revision++;
                _cuts.Clear();
                _groupCuts.Clear();
            }
        }
        // Dependencies admitted by the previous complete feedback snapshot must
        // be read even when traversal is still drawing their resident ancestor.
        if (_admitted is { } admitted)
            foreach (var pair in admitted)
                if (!_resident.TryGetValue(pair.Key, out var page) || !page.Root) Demand(pair.Key, pair.Value);
        _deferredGroups = checked(_deferredGroups + _budgetActiveDenied);
    }

    private void EndBudgetFrame()
    {
        MergeRecoveryRequests();
        if (!_budgetEnabled || _budgetPending) return;
        if (SameBudgetRequests()) { _budgetCacheHits++; return; }
        var started = Stopwatch.GetTimestamp();
        try {
        if (!_budgetHasRootShape) {
            var shape = PbrGpuScene.RootShape(_source, SharedInstances);
            _budgetRootVertices = shape.Vertices;
            _budgetRootTriangles = shape.Triangles;
            _budgetRootKeys = [.. _resident.Where(pair => pair.Value.Root).Select(pair => pair.Key)];
            _budgetHasRootShape = true;
        }
        var vertices = _budgetRootVertices;
        var triangles = _budgetRootTriangles;
        if (vertices > _arena.VertexCapacity || triangles > _arena.TriangleCapacity)
            throw new InvalidOperationException("Budget roots and transparent bootstrap exceed the geometry arena.");
        var next = _budgetNext ??= [];
        next.Clear();
        if (SharedInstances) { _budgetSelectedNodes.Begin(); _budgetPlanPages.Begin(); }
        foreach (var key in _budgetRootKeys) {
            next.Add(key, 0);
            if (SharedInstances) _budgetPlanPages.Add(key.Page);
        }
        _budgetOrder.Clear();
        foreach (var pair in _budgetRequests) {
            var child = pair.Key;
            var parent = _budgetLayout.ParentLocal(child.Asset, child.Node);
            if (parent < 0) throw new InvalidDataException("A geometry child request must have a parent.");
            var key = child with { Node = parent };
            var group = FindBudgetGroup(key, create: true)!;
            if (group.Frame != _clock) {
                group.Frame = _clock;
                group.Priority = 0;
                group.Recovery = _recovery?.Groups.Contains(key) ?? false;
                group.Children.Clear();
                _budgetOrder.Add(group);
            }
            group.Children.Add(child);
            group.Priority = System.Math.Max(group.Priority, pair.Value);
        }
        _budgetOrder.Sort(static (left, right) => {
            var recovery = right.Recovery.CompareTo(left.Recovery);
            if (recovery != 0) return recovery;
            var order = right.Priority.CompareTo(left.Priority);
            if (order != 0) return order;
            order = left.Key.Instance.CompareTo(right.Key.Instance);
            if (order != 0) return order;
            order = left.Key.Asset.CompareTo(right.Key.Asset);
            return order != 0 ? order : left.Key.Node.CompareTo(right.Key.Node);
        });
        _budgetSelected.Clear();
        var denied = 0;
        _budgetMissingAncestors = _budgetVertexDenied = _budgetTriangleDenied = 0;
        foreach (var group in _budgetOrder) {
            if (BudgetGroupSelected(group)) continue;
            _budgetClosure.Clear();
            var ancestor = group;
            var complete = true;
            while (!BudgetGroupSelected(ancestor)) {
                _budgetClosure.Add(ancestor);
                var parent = _budgetLayout.ParentLocal(ancestor.Key.Asset, ancestor.Key.Node);
                if (parent < 0) break;
                // A descendant is reachable only through the complete observed
                // child group of every ancestor, including its visible siblings.
                var dependency = FindBudgetGroup(ancestor.Key with { Node = parent });
                if (!_budgetRequests.ContainsKey(ancestor.Key) || dependency is null
                    || dependency.Frame != _clock) { complete = false; break; }
                ancestor = dependency;
            }
            if (!complete) { denied++; _budgetMissingAncestors++; continue; }
            if (!CollectBudgetPages(next, _arena.VertexCapacity - vertices, _arena.TriangleCapacity - triangles,
                out var extraVertices, out var extraTriangles)) {
                denied++;
                if (extraVertices > _arena.VertexCapacity - vertices) _budgetVertexDenied++;
                if (extraTriangles > _arena.TriangleCapacity - triangles) _budgetTriangleDenied++;
                continue;
            }
            vertices += extraVertices;
            triangles += extraTriangles;
            // Sorted priorities propagate from a selected group into its entire
            // dependency closure. Later groups can only request a lower priority.
            foreach (var key in _budgetPageList) {
                next.Add(key, group.Priority);
                if (SharedInstances) _budgetPlanPages.Add(key.Page);
            }
            foreach (var dependency in _budgetClosure) {
                if (SharedInstances) _budgetSelectedNodes.Add(dependency.Slot);
                else _budgetSelected.Add(dependency.Key);
            }
        }
        (_budgetPreviousRequests, _budgetRequests) = (_budgetRequests, _budgetPreviousRequests);
        _budgetVertexCapacity = _arena.VertexCapacity;
        _budgetTriangleCapacity = _arena.TriangleCapacity;
        _budgetHasSignature = true;
        _budgetNextDenied = denied;
        _budgetPending = true;
        }
        finally {
            _budgetRebuilds++;
            _budgetMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private bool CollectBudgetPages(Dictionary<Key, float> next, ulong vertexLimit, ulong triangleLimit,
        out ulong vertices, out ulong triangles)
    {
        _budgetGroupPages.Clear();
        _budgetPageList.Clear();
        if (SharedInstances) _budgetGroupPageMarks.Begin();
        vertices = triangles = 0;
        foreach (var dependency in _budgetClosure)
            foreach (var child in dependency.Children)
                foreach (var pageIndex in _budgetLayout.Pages(child.Asset, child.Node)) {
                    _budgetPageVisits++;
                    var key = new Key(SharedInstances ? -1 : child.Instance, pageIndex);
                    if (SharedInstances) {
                        if (_budgetPlanPages.Contains(pageIndex) || !_budgetGroupPageMarks.Add(pageIndex)) continue;
                    }
                    else if (next.ContainsKey(key) || !_budgetGroupPages.Add(key)) continue;
                    _budgetPageList.Add(key);
                    var page = _pages[key.Page];
                    vertices = checked(vertices + (ulong)page.Vertices);
                    triangles = checked(triangles + (ulong)page.Triangles);
                    // Costs are nonnegative. A failed prefix cannot become a
                    // fitting complete group, and no partial set is published.
                    if (vertices > vertexLimit || triangles > triangleLimit) return false;
                }
        return true;
    }

    private ReadOnlySpan<PbrSceneStream.PagePart> NodePages(ChildRequest child)
    {
        var tree = _source.Hierarchies[child.Asset];
        return tree.GetPages(tree.Nodes[child.Node]);
    }

    private bool SameBudgetRequests()
    {
        if (!_budgetHasSignature || _budgetVertexCapacity != _arena.VertexCapacity
            || _budgetTriangleCapacity != _arena.TriangleCapacity
            || _budgetRequests.Count != _budgetPreviousRequests.Count) return false;
        foreach (var pair in _budgetRequests)
            if (!_budgetPreviousRequests.TryGetValue(pair.Key, out var priority) || pair.Value != priority) return false;
        return true;
    }
}
