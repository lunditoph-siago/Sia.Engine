using System.Runtime.InteropServices;
using Sia.Asset;

namespace Sia.Engine.Mesh;

public sealed partial class MeshVirtualPagesData
{
    /// <summary>Pure source derivation. Returned metadata/pages own their arrays; this performs no IO or World mutation.</summary>
    public static (MeshVirtualPagesData Index, IReadOnlyDictionary<AssetId, MeshVirtualPageData> Pages) Build(
        MeshSource source, AssetId productId, MeshPatchBuildSettings? settings = null,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!productId.IsValid) throw new ArgumentException("Virtual product requires a logical ID.", nameof(productId));
        cancellationToken.ThrowIfCancellationRequested();
        var options = settings ?? MeshPatchBuildSettings.Default;
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        if (source.Sections.Length > MaximumParts) throw new ArgumentException("Virtual Mesh exceeds its material-section bound.", nameof(source));
        var parts = new List<MeshVirtualPagePart>(); var nodes = new List<MeshVirtualPageNode>();
        var ranges = new List<MeshVirtualPageRange>(); var descriptors = new List<MeshVirtualPageDescriptor>();
        var pages = new Dictionary<AssetId, MeshVirtualPageData>();
        ulong ownedPages = 0;
        foreach (var section in source.Sections) {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedPages >= maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            var compact = MeshPatchBuilder.Compact(source.Vertices,
                source.Indices.Slice(section.FirstIndex, section.IndexCount), source.BakeUvs, maximumBytes - ownedPages, cancellationToken);
            var build = MeshPatchBuilder.Build(compact, options, cancellationToken);
            var tree = build.Tree; var firstNode = nodes.Count;
            if (tree.PayloadBytes > maximumBytes - ownedPages)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Virtual hierarchy exceeds its owned-array budget.");
            var nodeRanges = new List<MeshVirtualPageRange>[tree.Nodes.Length];
            var depths = new int[tree.Nodes.Length];
            for (var i = 0; i < tree.Nodes.Length; i++) {
                nodeRanges[i] = [];
                var parent = tree.Nodes.Span[i].Parent;
                depths[i] = parent < 0 ? 0 : depths[parent] + 1;
            }
            var indices = new List<uint>(); var spans = new List<(int Node, int First, int Count)>();
            foreach (var level in Enumerable.Range(0, tree.Nodes.Length).GroupBy(i => depths[i]).OrderBy(g => g.Key)) {
                foreach (var n in level) {
                    var node = tree.Nodes.Span[n];
                    for (var triangle = 0; triangle < node.TriangleCount;) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = System.Math.Min(MeshVirtualPageData.MaximumTriangles - indices.Count / 3, node.TriangleCount - triangle);
                        spans.Add((n, indices.Count / 3, count));
                        for (var j = 0; j < count * 3; j++) indices.Add(tree.Indices[(node.TriangleOffset + triangle) * 3 + j]);
                        triangle += count;
                        if (indices.Count / 3 == MeshVirtualPageData.MaximumTriangles) Flush();
                    }
                }
                Flush(); // Coarse roots never require reading a finer hierarchy level.
            }
            for (var i = 0; i < tree.Nodes.Length; i++) {
                var node = tree.Nodes.Span[i];
                nodes.Add(new(node.Bounds, node.EstimatedSpatialError, node.Parent, node.ChildOffset, node.ChildCount,
                    ranges.Count, nodeRanges[i].Count, node.TriangleCount));
                ranges.AddRange(nodeRanges[i]);
            }
            parts.Add(new(section.MaterialSlot, section.FirstIndex / 3, build.SourceTriangleCount,
                firstNode, tree.Nodes.Length, tree.RootCount, tree.FinestTriangleCount));
            if (nodes.Count > MaximumNodes || ranges.Count > MaximumRanges)
                throw new ArgumentOutOfRangeException(nameof(source), "Virtual index exceeds its hierarchy limits.");

            void Flush()
            {
                if (indices.Count == 0) return;
                cancellationToken.ThrowIfCancellationRequested();
                if (pages.Count >= MaximumPages) throw new ArgumentOutOfRangeException(nameof(source), "Too many virtual pages.");
                if (ownedPages >= maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                var data = MeshVirtualPageData.Create(tree.Vertices, CollectionsMarshal.AsSpan(indices), tree.BakeUvs,
                    System.Math.Min((ulong)MeshVirtualPageData.MaximumEncodedBytes, maximumBytes - ownedPages), cancellationToken);
                var index = descriptors.Count; var id = PageId(productId, index);
                descriptors.Add(new(id, data.ContentHash, data.EncodedBytes, data.Vertices.Length, data.TriangleCount, data.Bounds));
                pages.Add(id, data); ownedPages += data.PayloadBytes;
                foreach (var span in spans) nodeRanges[span.Node].Add(new(firstNode + span.Node, index, span.First, span.Count));
                indices.Clear(); spans.Clear();
            }
        }
        var result = new MeshVirtualPagesData(productId, source.ContentHash, options, source.Vertices.Length,
            source.Indices.Length / 3, source.HasBakeUvs, source.Bounds, [.. parts], [.. nodes], [.. ranges], [.. descriptors], cancellationToken);
        if (result.PayloadBytes > maximumBytes - ownedPages || result.EncodedBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Virtual index/pages exceed their owned-array budget.");
        return (result, pages);
    }
}
