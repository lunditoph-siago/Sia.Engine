using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public readonly record struct MeshVirtualPagePart(int MaterialSlot, int FirstSourceTriangle, int SourceTriangleCount,
    int FirstNode, int NodeCount, int RootCount, int FinestTriangleCount);

public readonly record struct MeshVirtualPageNode(Aabb Bounds, float Error, int Parent, int FirstChild, int ChildCount,
    int FirstRange, int RangeCount, int TriangleCount);

public readonly record struct MeshVirtualPageRange(int Node, int Page, int FirstTriangle, int TriangleCount);

public readonly record struct MeshVirtualPageDescriptor(AssetId Id, string ContentHash, int EncodedBytes,
    int VertexCount, int TriangleCount, Aabb Bounds)
{
    public AssetRefer<MeshVirtualPageRecord>.Id Reference => new(Id);
    public AssetContent<MeshVirtualPageRecord> Content => new(Id, [new AssetChunk(ContentHash, EncodedBytes)]);
}

/// <summary>Mesh-local virtual hierarchy and typed page references; no page payload, shading or instances.</summary>
public sealed partial class MeshVirtualPagesData
{
    public const int MaximumParts = 4096;
    public const int MaximumPages = 65536;
    public const int MaximumNodes = 1000000;
    public const int MaximumRanges = 1000000;
    public const int MaximumEncodedBytes = 64 * 1024 * 1024;
    private readonly MeshVirtualPagePart[] _parts;
    private readonly MeshVirtualPageNode[] _nodes;
    private readonly MeshVirtualPageRange[] _ranges;
    private readonly MeshVirtualPageDescriptor[] _pages;
    private readonly int[] _pageOffsets;
    private readonly int[] _pageUses;
    public AssetId ProductId { get; }
    public string SourceHash { get; }
    public string BuildKey { get; }
    public MeshPatchBuildSettings Settings { get; }
    public int SourceVertexCount { get; }
    public int SourceTriangleCount { get; }
    public bool HasBakeUvs { get; }
    public Aabb Bounds { get; }
    public ReadOnlySpan<MeshVirtualPagePart> Parts => _parts;
    public ReadOnlySpan<MeshVirtualPageNode> Nodes => _nodes;
    public ReadOnlySpan<MeshVirtualPageRange> Ranges => _ranges;
    public ReadOnlySpan<MeshVirtualPageDescriptor> Pages => _pages;
    public ulong PayloadBytes => OwnedBytes(_parts.Length, _nodes.Length, _ranges.Length, _pages.Length);
    public ulong EncodedBytes => WireBytes(_parts.Length, _nodes.Length, _ranges.Length, _pages.Length);

    private MeshVirtualPagesData(AssetId productId, string sourceHash, MeshPatchBuildSettings settings,
        int vertices, int triangles, bool bake, Aabb bounds, MeshVirtualPagePart[] parts, MeshVirtualPageNode[] nodes,
        MeshVirtualPageRange[] ranges, MeshVirtualPageDescriptor[] pages, CancellationToken token)
    {
        ProductId = productId; SourceHash = sourceHash; Settings = settings;
        SourceVertexCount = vertices; SourceTriangleCount = triangles; HasBakeUvs = bake; Bounds = bounds;
        _parts = parts; _nodes = nodes; _ranges = ranges; _pages = pages;
        Validate(token);
        BuildKey = ComputeBuildKey(sourceHash, settings);
        _pageOffsets = new int[pages.Length + 1]; _pageUses = new int[ranges.Length];
        foreach (var range in ranges) _pageOffsets[range.Page + 1]++;
        for (var i = 1; i < _pageOffsets.Length; i++) _pageOffsets[i] += _pageOffsets[i - 1];
        var cursor = _pageOffsets.ToArray();
        for (var i = 0; i < ranges.Length; i++) _pageUses[cursor[ranges[i].Page]++] = i;
        for (var page = 0; page < pages.Length; page++) {
            token.ThrowIfCancellationRequested();
            var nextTriangle = 0;
            foreach (var use in PageUses(page)) {
                var range = ranges[use];
                Require(range.FirstTriangle == nextTriangle, "Virtual page ranges overlap or leave gaps.");
                nextTriangle += range.TriangleCount;
            }
            Require(nextTriangle == pages[page].TriangleCount, "Unused or partially referenced virtual page.");
        }
    }

    /// <summary>Checks a prepared page against its parent before the caller publishes its typed record.</summary>
    public void ValidatePage(int index, MeshVirtualPageData page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if ((uint)index >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var descriptor = _pages[index];
        Require(page.ContentHash == descriptor.ContentHash && page.EncodedBytes == descriptor.EncodedBytes
            && page.Vertices.Length == descriptor.VertexCount && page.TriangleCount == descriptor.TriangleCount
            && page.HasBakeUvs == HasBakeUvs && page.Bounds == descriptor.Bounds, "Virtual page differs from its parent descriptor.");
        foreach (var use in PageUses(index)) {
            cancellationToken.ThrowIfCancellationRequested();
            var range = _ranges[use]; var bounds = _nodes[range.Node].Bounds;
            foreach (var vertex in page.Indices.Slice(range.FirstTriangle * 3, range.TriangleCount * 3))
                Require(Contains(bounds, page.Vertices[(int)vertex].Position), "Virtual node bounds exclude a page triangle.");
        }
    }

    public ReadOnlySpan<int> PageUses(int page)
    {
        if ((uint)page >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(page));
        return _pageUses.AsSpan(_pageOffsets[page], _pageOffsets[page + 1] - _pageOffsets[page]);
    }

    private void Validate(CancellationToken token)
    {
        Require(ProductId.IsValid && Settings.IsValid && SourceVertexCount > 0 && SourceTriangleCount > 0 && Valid(Bounds)
            && _parts.Length is > 0 and <= MaximumParts && _nodes.Length <= MaximumNodes
            && _ranges.Length <= MaximumRanges && _pages.Length <= MaximumPages && EncodedBytes <= MaximumEncodedBytes,
            "Invalid virtual page index summary.");
        long sourceTriangles = 0; var nextNode = 0; var nextRange = 0;
        foreach (var part in _parts) {
            token.ThrowIfCancellationRequested();
            Require(part.MaterialSlot >= 0 && part.FirstSourceTriangle == sourceTriangles && part.SourceTriangleCount > 0
                && part.FirstNode == nextNode && part.NodeCount >= 0 && part.NodeCount <= _nodes.Length - nextNode
                && part.RootCount >= 0 && part.RootCount <= part.NodeCount && (part.NodeCount == 0) == (part.RootCount == 0)
                && part.FinestTriangleCount >= 0 && part.FinestTriangleCount <= part.SourceTriangleCount,
                "Invalid virtual part coverage.");
            var nodes = _nodes.AsSpan(nextNode, part.NodeCount);
            var nextChild = part.RootCount; long finest = 0;
            var depths = new int[nodes.Length];
            for (var i = 0; i < nodes.Length; i++) {
                token.ThrowIfCancellationRequested();
                var node = nodes[i];
                Require(Valid(node.Bounds) && Contains(Bounds, node.Bounds.Min) && Contains(Bounds, node.Bounds.Max)
                    && float.IsFinite(node.Error) && node.Error >= 0 && (node.ChildCount != 0 || node.Error == 0)
                    && (i < part.RootCount ? node.Parent == -1 : node.Parent >= 0 && node.Parent < i)
                    && node.FirstChild == nextChild && node.ChildCount >= 0 && node.ChildCount <= nodes.Length - nextChild
                    && node.FirstRange == nextRange && node.RangeCount > 0 && node.RangeCount <= _ranges.Length - nextRange
                    && node.TriangleCount > 0, "Invalid virtual hierarchy node.");
                if (node.Parent >= 0) depths[i] = depths[node.Parent] + 1;
                Require(depths[i] <= 64, "Virtual hierarchy exceeds its traversal depth.");
                long childTriangles = 0;
                for (var child = nextChild; child < nextChild + node.ChildCount; child++) {
                    var value = nodes[child];
                    Require(child > i && value.Parent == i && value.Error <= node.Error
                        && Contains(node.Bounds, value.Bounds.Min) && Contains(node.Bounds, value.Bounds.Max),
                        "Virtual parents must cover ordered children and their errors.");
                    childTriangles += value.TriangleCount;
                }
                Require(node.ChildCount == 0 || node.TriangleCount <= childTriangles, "Virtual parent increases triangle count.");
                long triangles = 0;
                for (var r = nextRange; r < nextRange + node.RangeCount; r++) {
                    var range = _ranges[r];
                    Require(range.Node == nextNode + i && (uint)range.Page < _pages.Length && range.FirstTriangle >= 0
                        && range.TriangleCount > 0 && (long)range.FirstTriangle + range.TriangleCount <= _pages[range.Page].TriangleCount,
                        "Invalid typed virtual page range.");
                    triangles += range.TriangleCount;
                }
                Require(triangles == node.TriangleCount, "Virtual page ranges differ from node triangle count.");
                nextRange += node.RangeCount; nextChild += node.ChildCount;
                if (node.ChildCount == 0) finest += node.TriangleCount;
            }
            Require(nextChild == nodes.Length && finest == part.FinestTriangleCount, "Incomplete virtual forest or finest coverage.");
            sourceTriangles += part.SourceTriangleCount; nextNode += part.NodeCount;
        }
        Require(sourceTriangles == SourceTriangleCount && nextNode == _nodes.Length && nextRange == _ranges.Length,
            "Unused virtual metadata or incomplete source coverage.");
        for (var i = 0; i < _pages.Length; i++) {
            token.ThrowIfCancellationRequested(); var page = _pages[i];
            Require(page.Id == PageId(ProductId, i) && page.Id != ProductId && page.ContentHash is { Length: 64 }
                && page.ContentHash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')
                && page.VertexCount is > 0 and <= MeshVirtualPageData.MaximumTriangles * 3
                && page.TriangleCount is > 0 and <= MeshVirtualPageData.MaximumTriangles && page.VertexCount <= page.TriangleCount * 3
                && page.EncodedBytes == 73L + page.VertexCount * (HasBakeUvs ? 56L : 48L) + page.TriangleCount * 12L
                && page.EncodedBytes <= MeshVirtualPageData.MaximumEncodedBytes && Valid(page.Bounds)
                && Contains(Bounds, page.Bounds.Min) && Contains(Bounds, page.Bounds.Max), "Invalid virtual page descriptor or slot identity.");
        }
    }

    private static AssetId PageId(AssetId product, int ordinal)
    {
        Span<byte> key = stackalloc byte[20]; product.Value.TryWriteBytes(key[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32LittleEndian(key[16..], ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHVIRTUALPAGESLOT"u8); hash.AppendData(key);
        var digest = hash.GetHashAndReset(); digest[6] = (byte)((digest[6] & 15) | 128); digest[8] = (byte)((digest[8] & 63) | 128);
        return new(new Guid(digest.AsSpan(0, 16), bigEndian: true));
    }

    private static string ComputeBuildKey(string sourceHash, MeshPatchBuildSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHVIRTUALPAGES"u8);
        using var derivation = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        derivation.AppendData("SIAMESHVIRTUAL"u8);
        derivation.AppendData(Convert.FromHexString(sourceHash));
        Span<byte> options = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(options, settings.MaxLeafTriangles);
        BinaryPrimitives.WriteInt32LittleEndian(options[4..], settings.MaxChildren);
        BinaryPrimitives.WriteSingleLittleEndian(options[8..], settings.ParentTriangleRatio);
        BinaryPrimitives.WriteSingleLittleEndian(options[12..], settings.NormalWeight);
        BinaryPrimitives.WriteSingleLittleEndian(options[16..], settings.UVWeight);
        derivation.AppendData(options);
        hash.AppendData(derivation.GetHashAndReset());
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, MeshVirtualPageData.MaximumTriangles);
        hash.AppendData(size); return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static ulong WireBytes(int parts, int nodes, int ranges, int pages)
        => 189ul + (ulong)parts * 28 + (ulong)nodes * 52 + (ulong)ranges * 16 + (ulong)pages * 84;
    private static ulong OwnedBytes(int parts, int nodes, int ranges, int pages)
        => (ulong)parts * (ulong)Unsafe.SizeOf<MeshVirtualPagePart>() + (ulong)nodes * (ulong)Unsafe.SizeOf<MeshVirtualPageNode>()
            + (ulong)ranges * (ulong)Unsafe.SizeOf<MeshVirtualPageRange>() + (ulong)pages * (ulong)Unsafe.SizeOf<MeshVirtualPageDescriptor>()
            + (ulong)(ranges + (long)pages + 1) * sizeof(int);
    private static bool Valid(Aabb bounds) => math.all(math.isfinite(bounds.Min)) && math.all(math.isfinite(bounds.Max))
        && math.all(bounds.Min <= bounds.Max);
    private static bool Contains(Aabb bounds, float3 point) => math.all(point >= bounds.Min) && math.all(point <= bounds.Max);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
