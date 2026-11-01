namespace Sia.Engine.Rendering.Pbr;

/// <summary>Owner-local immutable numeric dependencies; source codecs and page identities are unchanged.</summary>
internal sealed class PbrGeometryAdmissionLayout
{
    private readonly int[] _firstNodes, _parents, _firstPages, _pages;
    internal int NodeCount => _parents.Length;

    internal PbrGeometryAdmissionLayout(PbrSceneStream.HierarchyInfo[] hierarchies, ReadOnlySpan<int> canonicalPages)
    {
        _firstNodes = new int[checked(hierarchies.Length + 1)];
        for (var h = 0; h < hierarchies.Length; h++)
            _firstNodes[h + 1] = checked(_firstNodes[h] + hierarchies[h].Nodes.Length);
        _parents = new int[_firstNodes[^1]]; _firstPages = new int[checked(NodeCount + 1)];
        var pages = new List<int>();
        var seen = new int[canonicalPages.Length]; Array.Fill(seen, -1);
        for (var h = 0; h < hierarchies.Length; h++) {
            var tree = hierarchies[h]; var first = _firstNodes[h];
            for (var n = 0; n < tree.Nodes.Length; n++) {
                var node = tree.Nodes[n]; var slot = first + n;
                if (node.Parent < -1 || node.Parent >= n)
                    throw new ArgumentException("Admission parents must precede their children.", nameof(hierarchies));
                _parents[slot] = node.Parent < 0 ? -1 : first + node.Parent;
                _firstPages[slot] = pages.Count;
                foreach (var part in tree.GetPages(node)) {
                    if ((uint)part.Page >= (uint)canonicalPages.Length)
                        throw new ArgumentException("Admission range references a missing source page.", nameof(hierarchies));
                    var page = canonicalPages[part.Page];
                    if ((uint)page >= (uint)seen.Length)
                        throw new ArgumentException("Invalid canonical page identity.", nameof(canonicalPages));
                    if (seen[page] == slot) continue;
                    seen[page] = slot; pages.Add(page);
                }
            }
        }
        _firstPages[^1] = pages.Count; _pages = pages.ToArray();
    }

    internal int Node(int asset, int node) => _firstNodes[asset] + node;
    internal int ParentLocal(int asset, int node)
    {
        var parent = _parents[Node(asset, node)];
        return parent < 0 ? -1 : parent - _firstNodes[asset];
    }
    internal ReadOnlySpan<int> Pages(int asset, int node)
    {
        var slot = Node(asset, node);
        return _pages.AsSpan(_firstPages[slot], _firstPages[slot + 1] - _firstPages[slot]);
    }
}

/// <summary>Bounded scratch membership without clearing a full table for each closure.</summary>
internal sealed class PbrAdmissionEpochSet
{
    private readonly uint[] _stamps;
    private uint _generation;
    internal PbrAdmissionEpochSet(int capacity, uint generation = 0)
        => (_stamps, _generation) = (new uint[capacity], generation);
    internal void Begin()
    {
        _generation = unchecked(_generation + 1);
        if (_generation != 0) return;
        Array.Clear(_stamps); _generation = 1;
    }
    internal bool Contains(int index) => _generation != 0 && _stamps[index] == _generation;
    internal bool Add(int index)
    {
        if (_generation == 0) throw new InvalidOperationException("Begin an admission epoch before using its scratch set.");
        if (_stamps[index] == _generation) return false;
        _stamps[index] = _generation; return true;
    }
}
