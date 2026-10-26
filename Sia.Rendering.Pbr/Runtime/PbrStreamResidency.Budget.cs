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
    }

    private Dictionary<Key, float>? _admitted, _budgetNext;
    private Dictionary<ChildRequest, float> _budgetRequests = [], _budgetPreviousRequests = [];
    private readonly Dictionary<ChildRequest, BudgetGroup> _budgetGroups = [];
    private readonly List<BudgetGroup> _budgetOrder = [], _budgetClosure = [];
    private readonly HashSet<ChildRequest> _budgetSelected = [];
    private readonly HashSet<Key> _budgetGroupPages = [];
    private Dictionary<ChildRequest, float>? _viewRequests;
    private Key[] _budgetRootKeys = [];
    private ulong _budgetRootVertices, _budgetRootTriangles;
    private uint _budgetVertexCapacity, _budgetTriangleCapacity;
    private bool _budgetEnabled, _budgetHasRootShape, _budgetHasSignature, _budgetPending;
    private int _budgetActiveDenied, _budgetNextDenied;

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
        if (!_budgetEnabled || _budgetPending || SameBudgetRequests()) return;
        if (!_budgetHasRootShape) {
            var shape = PbrGpuScene.RootShape(_source, _settings.GpuTraversal);
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
        foreach (var key in _budgetRootKeys) next.Add(key, 0);
        _budgetOrder.Clear();
        foreach (var pair in _budgetRequests) {
            var child = pair.Key;
            var parent = _source.Hierarchies[child.Asset].Nodes[child.Node].Parent;
            if (parent < 0) throw new InvalidDataException("A geometry child request must have a parent.");
            var key = child with { Node = parent };
            if (!_budgetGroups.TryGetValue(key, out var group)) _budgetGroups.Add(key, group = new(key));
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
        foreach (var group in _budgetOrder) {
            if (_budgetSelected.Contains(group.Key)) continue;
            _budgetClosure.Clear();
            var ancestor = group;
            var complete = true;
            while (!_budgetSelected.Contains(ancestor.Key)) {
                _budgetClosure.Add(ancestor);
                var parent = _source.Hierarchies[ancestor.Key.Asset].Nodes[ancestor.Key.Node].Parent;
                if (parent < 0) break;
                // A descendant is reachable only through the complete observed
                // child group of every ancestor, including its visible siblings.
                if (!_budgetRequests.ContainsKey(ancestor.Key)
                    || !_budgetGroups.TryGetValue(ancestor.Key with { Node = parent }, out var dependency)
                    || dependency.Frame != _clock) { complete = false; break; }
                ancestor = dependency;
            }
            if (!complete) { denied++; continue; }
            if (!CollectBudgetPages(next, _arena.VertexCapacity - vertices, _arena.TriangleCapacity - triangles,
                out var extraVertices, out var extraTriangles)) {
                denied++;
                continue;
            }
            vertices += extraVertices;
            triangles += extraTriangles;
            // Sorted priorities propagate from a selected group into its entire
            // dependency closure. Later groups can only request a lower priority.
            foreach (var key in _budgetGroupPages) next.Add(key, group.Priority);
            foreach (var dependency in _budgetClosure) _budgetSelected.Add(dependency.Key);
        }
        (_budgetPreviousRequests, _budgetRequests) = (_budgetRequests, _budgetPreviousRequests);
        _budgetVertexCapacity = _arena.VertexCapacity;
        _budgetTriangleCapacity = _arena.TriangleCapacity;
        _budgetHasSignature = true;
        _budgetNextDenied = denied;
        _budgetPending = true;
    }

    private bool CollectBudgetPages(Dictionary<Key, float> next, ulong vertexLimit, ulong triangleLimit,
        out ulong vertices, out ulong triangles)
    {
        _budgetGroupPages.Clear();
        vertices = triangles = 0;
        foreach (var dependency in _budgetClosure)
            foreach (var child in dependency.Children)
                foreach (var part in _source.Hierarchies[child.Asset].Nodes[child.Node].Pages) {
                    var key = PageKey(child.Instance, part.Id);
                    if (next.ContainsKey(key) || !_budgetGroupPages.Add(key)) continue;
                    var page = _pages[key.Page];
                    vertices = checked(vertices + (ulong)page.Vertices);
                    triangles = checked(triangles + (ulong)page.Triangles);
                    // Costs are nonnegative. A failed prefix cannot become a
                    // fitting complete group, and no partial set is published.
                    if (vertices > vertexLimit || triangles > triangleLimit) return false;
                }
        return true;
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
