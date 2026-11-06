using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    internal readonly record struct ChildRequest(int Instance, int Asset, int Node);

    private Dictionary<Key, float>? _admitted, _budgetNext;
    private Dictionary<ChildRequest, float> _budgetRequests = [], _budgetPreviousRequests = [];
    private BudgetWorkspace _budgetWorkspace = null!;
    private Dictionary<ChildRequest, float>? _viewRequests;
    private BudgetPage[] _budgetRootPages = [];
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
        => _budgetWorkspace = new(_source.Hierarchies, _canonicalPages, _pages, SharedInstances);

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
        ref var previous = ref CollectionsMarshal.GetValueRefOrAddDefault(_budgetRequests, child, out var found);
        if (!found || priority > previous) previous = priority;
        if (_viewRequests is { } requests) {
            ref var viewPriority = ref CollectionsMarshal.GetValueRefOrAddDefault(requests, child, out found);
            if (!found || priority > viewPriority) viewPriority = priority;
        }
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
                _budgetRootPages = [.. _resident.Where(pair => pair.Value.Root)
                    .Select(pair => new BudgetPage(pair.Key.Instance, pair.Key.Page, 0))];
                _budgetHasRootShape = true;
            }
            var counts = _budgetWorkspace.Plan(_budgetRequests, _budgetRootPages,
                _budgetRootVertices, _budgetRootTriangles, _arena.VertexCapacity, _arena.TriangleCapacity,
                _clock, _recovery?.Groups);
            var next = _budgetNext ??= [];
            next.Clear();
            foreach (var page in _budgetWorkspace.Result) next.Add(new(page.Instance, page.Page), page.Priority);
            _budgetMissingAncestors = counts.MissingAncestors;
            _budgetVertexDenied = counts.VertexDenied; _budgetTriangleDenied = counts.TriangleDenied;
            _budgetPageVisits += counts.PageVisits;
            (_budgetPreviousRequests, _budgetRequests) = (_budgetRequests, _budgetPreviousRequests);
            _budgetVertexCapacity = _arena.VertexCapacity;
            _budgetTriangleCapacity = _arena.TriangleCapacity;
            _budgetHasSignature = true;
            _budgetNextDenied = counts.Denied;
            _budgetPending = true;
        }
        finally {
            _budgetRebuilds++;
            _budgetMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
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
        return _budgetWorkspace.SameRequests(_budgetRequests, _budgetPreviousRequests);
    }
}
