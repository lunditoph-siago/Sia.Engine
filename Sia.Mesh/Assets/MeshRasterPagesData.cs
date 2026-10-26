using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public readonly record struct MeshRasterPagesSection(int MaterialSlot, int FirstIndex, int IndexCount,
    int FirstLod, int LodCount, Aabb Bounds);
public readonly record struct MeshRasterPagesLod(int FirstIndex, int IndexCount, float Error, int FirstPage, int PageCount);
public readonly record struct MeshRasterPageDescriptor(AssetId Id, string ContentHash, int EncodedBytes, MeshRasterPageKind Kind,
    int FirstElement, int ElementCount, int FirstDependency, int DependencyCount)
{
    public AssetRefer<MeshRasterPageRecord>.Id Reference => new(Id);
    public AssetContent<MeshRasterPageRecord> Content => new(Id, [new AssetChunk(ContentHash, EncodedBytes)]);
}

/// <summary>Exact existing Raster LOD metadata and typed pages; owns no Raster/page payload or IO lifetime.</summary>
public sealed partial class MeshRasterPagesData
{
    public const int MaximumSections = 4096;
    public const int MaximumLods = MaximumSections * 8;
    public const int MaximumPages = 65536;
    public const int MaximumDependencies = 1_000_000;
    public const int MaximumEncodedBytes = 64 * 1024 * 1024;
    private readonly MeshRasterPagesSection[] _sections;
    private readonly MeshRasterPagesLod[] _lods;
    private readonly MeshRasterPageDescriptor[] _pages;
    private readonly int[] _vertexDependencies;
    public AssetId ProductId { get; }
    public MeshProductReference<MeshRasterRecord> Raster { get; }
    public string SourceHash { get; }
    public string RasterContentHash { get; }
    public ulong RasterEncodedBytes { get; }
    public string BuildKey { get; }
    public MeshRasterBuildSettings Settings { get; }
    public Aabb Bounds { get; }
    public int SourceVertexCount { get; }
    public int SourceTriangleCount { get; }
    public int TotalIndexCount { get; }
    public bool HasBakeUvs { get; }
    public int VertexPageCount { get; }
    public ReadOnlySpan<MeshRasterPagesSection> Sections => _sections;
    public ReadOnlySpan<MeshRasterPagesLod> Lods => _lods;
    public ReadOnlySpan<MeshRasterPageDescriptor> Pages => _pages;
    public ReadOnlySpan<int> VertexDependencies => _vertexDependencies;
    public ulong EncodedBytes => WireBytes(_sections.Length, _lods.Length, _pages.Length, _vertexDependencies.Length);
    public ulong PayloadBytes => OwnedBytes(_sections.Length, _lods.Length, _pages.Length, _vertexDependencies.Length);

    private MeshRasterPagesData(AssetId productId, MeshProductReference<MeshRasterRecord> raster, string sourceHash,
        string rasterContentHash, ulong rasterEncodedBytes, MeshRasterBuildSettings settings, Aabb bounds,
        int sourceVertexCount, int sourceTriangleCount, int totalIndexCount, bool hasBakeUvs, int vertexPageCount,
        MeshRasterPagesSection[] sections, MeshRasterPagesLod[] lods, MeshRasterPageDescriptor[] pages,
        int[] vertexDependencies, CancellationToken token)
    {
        ProductId = productId; Raster = raster; SourceHash = sourceHash; RasterContentHash = rasterContentHash;
        RasterEncodedBytes = rasterEncodedBytes; Settings = settings; Bounds = bounds;
        SourceVertexCount = sourceVertexCount; SourceTriangleCount = sourceTriangleCount; TotalIndexCount = totalIndexCount;
        HasBakeUvs = hasBakeUvs; VertexPageCount = vertexPageCount;
        _sections = sections; _lods = lods; _pages = pages; _vertexDependencies = vertexDependencies;
        Validate(token); BuildKey = ComputeBuildKey(token);
    }

    public ReadOnlySpan<MeshRasterPagesLod> GetLods(int section)
    {
        if ((uint)section >= _sections.Length) throw new ArgumentOutOfRangeException(nameof(section));
        var value = _sections[section]; return _lods.AsSpan(value.FirstLod, value.LodCount);
    }

    public ReadOnlySpan<int> GetVertexDependencies(int page)
    {
        if ((uint)page >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(page));
        var value = _pages[page]; return _vertexDependencies.AsSpan(value.FirstDependency, value.DependencyCount);
    }

    /// <summary>Checks full content/shape and exact global-index dependency closure before acquisition or publication.</summary>
    public void ValidatePage(int index, MeshRasterPageData page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page); cancellationToken.ThrowIfCancellationRequested();
        if ((uint)index >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var descriptor = _pages[index];
        Require(page.Kind == descriptor.Kind && page.ContentHash == descriptor.ContentHash && page.EncodedBytes == descriptor.EncodedBytes
            && page.ElementCount == descriptor.ElementCount && page.HasBakeUvs == (descriptor.Kind == MeshRasterPageKind.Vertices && HasBakeUvs),
            "Raster page differs from its exact parent descriptor.");
        if (descriptor.Kind == MeshRasterPageKind.Vertices) {
            foreach (var vertex in page.Vertices) {
                cancellationToken.ThrowIfCancellationRequested();
                Require(Contains(Bounds, vertex.Position), "Raster vertex page exceeds source bounds.");
            }
        }
        else {
            var dependencies = new HashSet<int>();
            foreach (var vertex in page.Indices) {
                cancellationToken.ThrowIfCancellationRequested();
                Require(vertex < (uint)SourceVertexCount, "Raster index page references an absent source vertex ordinal.");
                dependencies.Add((int)(vertex / MeshRasterPageData.MaximumVertices));
            }
            var expected = dependencies.ToArray(); Array.Sort(expected);
            Require(GetVertexDependencies(index).SequenceEqual(expected), "Raster index page has missing or excess vertex-page dependencies.");
        }
    }

    private void Validate(CancellationToken token)
    {
        Require(ProductId.IsValid && Raster is not null && ProductId != Raster.Id && Hash(SourceHash) && Hash(RasterContentHash)
            && Settings.IsValid && Valid(Bounds) && SourceVertexCount > 0 && SourceTriangleCount > 0
            && (long)SourceTriangleCount * 3 <= TotalIndexCount && TotalIndexCount % 3 == 0,
            "Invalid exact Raster page provenance or source summary.");
        Require(Raster.BuildKey == MeshRasterData.ComputeBuildKey(SourceHash, Settings), "Raster derivation key differs from source/settings.");
        RequireCounts(_sections.Length, _lods.Length, _pages.Length, _vertexDependencies.Length, VertexPageCount);
        Require(VertexPageCount == ((long)SourceVertexCount + MeshRasterPageData.MaximumVertices - 1) / MeshRasterPageData.MaximumVertices,
            "Incomplete original vertex-page ordinal coverage.");
        var expectedRasterBytes = 169ul + (ulong)SourceVertexCount * (HasBakeUvs ? 56ul : 48ul) + (ulong)TotalIndexCount * 4
            + (ulong)_sections.Length * 12 + (ulong)_lods.Length * 12 + (ulong)(_sections.Length + 1) * 4;
        Require(RasterEncodedBytes == expectedRasterBytes, "Exact original Raster encoded size differs from metadata.");
        for (var page = 0; page < _pages.Length; page++) {
            token.ThrowIfCancellationRequested(); var value = _pages[page];
            Require(value.Id == PageId(ProductId, page) && value.Id != ProductId && value.Id != Raster.Id && Hash(value.ContentHash),
                "Invalid Raster page logical/content identity.");
            if (page < VertexPageCount) {
                var count = System.Math.Min(MeshRasterPageData.MaximumVertices, SourceVertexCount - page * MeshRasterPageData.MaximumVertices);
                Require(value.Kind == MeshRasterPageKind.Vertices && value.FirstElement == page * MeshRasterPageData.MaximumVertices
                    && value.ElementCount == count && value.FirstDependency == 0 && value.DependencyCount == 0
                    && value.EncodedBytes == 64 + count * (HasBakeUvs ? 56 : 48), "Invalid complete vertex-page shape.");
            }
            else Require(value.Kind == MeshRasterPageKind.Indices && value.ElementCount is > 0 and <= MeshRasterPageData.MaximumTriangles * 3
                && value.ElementCount % 3 == 0 && value.EncodedBytes == 64 + value.ElementCount * 4, "Invalid Raster index-page shape.");
        }
        long sourceCursor = 0, coarseCursor = (long)SourceTriangleCount * 3; var lodCursor = 0; var pageCursor = VertexPageCount; var dependencyCursor = 0;
        foreach (var section in _sections) {
            token.ThrowIfCancellationRequested();
            Require(section.MaterialSlot >= 0 && section.FirstIndex == sourceCursor && section.IndexCount > 0 && section.IndexCount % 3 == 0
                && section.FirstLod == lodCursor && section.LodCount is > 0 && section.LodCount <= Settings.MaximumLods
                && section.LodCount <= _lods.Length - lodCursor && Valid(section.Bounds)
                && Contains(Bounds, section.Bounds.Min) && Contains(Bounds, section.Bounds.Max), "Invalid ordered Raster section metadata.");
            var previousCount = 0; var previousError = 0f;
            for (var level = 0; level < section.LodCount; level++) {
                token.ThrowIfCancellationRequested(); var lod = _lods[lodCursor++];
                Require(lod.IndexCount > 0 && lod.IndexCount % 3 == 0 && float.IsFinite(lod.Error) && lod.Error >= previousError
                    && (level == 0 ? lod.FirstIndex == section.FirstIndex && lod.IndexCount == section.IndexCount && lod.Error == 0
                        : lod.FirstIndex == coarseCursor && lod.IndexCount < previousCount)
                    && lod.FirstPage == pageCursor && lod.PageCount == ((long)lod.IndexCount + MeshRasterPageData.MaximumTriangles * 3 - 1) / (MeshRasterPageData.MaximumTriangles * 3)
                    && lod.PageCount <= _pages.Length - pageCursor, "Invalid existing Raster LOD range/error or page coverage.");
                for (var offset = 0; offset < lod.IndexCount;) {
                    token.ThrowIfCancellationRequested(); var page = _pages[pageCursor++];
                    var count = System.Math.Min(MeshRasterPageData.MaximumTriangles * 3, lod.IndexCount - offset);
                    Require(page.Kind == MeshRasterPageKind.Indices && page.FirstElement == (long)lod.FirstIndex + offset && page.ElementCount == count
                        && page.FirstDependency == dependencyCursor && page.DependencyCount > 0
                        && page.DependencyCount <= System.Math.Min(count, VertexPageCount)
                        && page.DependencyCount <= _vertexDependencies.Length - dependencyCursor, "Invalid contiguous Raster index page or dependency slice.");
                    var previous = -1;
                    for (var dependency = 0; dependency < page.DependencyCount; dependency++) {
                        var vertexPage = _vertexDependencies[dependencyCursor++];
                        Require(vertexPage > previous && vertexPage < VertexPageCount, "Vertex-page dependencies must be sorted, unique and in range."); previous = vertexPage;
                    }
                    offset += count;
                }
                if (level != 0) coarseCursor += lod.IndexCount;
                previousCount = lod.IndexCount; previousError = lod.Error;
            }
            sourceCursor += section.IndexCount;
        }
        Require(sourceCursor == (long)SourceTriangleCount * 3 && coarseCursor == TotalIndexCount
            && lodCursor == _lods.Length && pageCursor == _pages.Length && dependencyCursor == _vertexDependencies.Length,
            "Unused exact Raster metadata or incomplete original LOD coverage.");
    }

    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool Valid(Aabb bounds) => math.all(math.isfinite(bounds.Min)) && math.all(math.isfinite(bounds.Max)) && math.all(bounds.Min <= bounds.Max);
    private static bool Contains(Aabb bounds, float3 point) => math.all(point >= bounds.Min) && math.all(point <= bounds.Max);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void RequireCounts(int sections, int lods, int pages, int dependencies, int vertexPages)
        => Require(sections is > 0 and <= MaximumSections && lods >= sections && lods <= MaximumLods
            && pages is > 0 and <= MaximumPages && dependencies is >= 0 and <= MaximumDependencies
            && vertexPages > 0 && vertexPages < pages, "Raster page index count limit exceeded.");
    private static ulong WireBytes(int sections, int lods, int pages, int dependencies)
        => 304ul + (ulong)sections * 44 + (ulong)lods * 20 + (ulong)pages * 72 + (ulong)dependencies * 4;
    private static ulong OwnedBytes(int sections, int lods, int pages, int dependencies)
        => (ulong)sections * (ulong)Unsafe.SizeOf<MeshRasterPagesSection>() + (ulong)lods * (ulong)Unsafe.SizeOf<MeshRasterPagesLod>()
            + (ulong)pages * (ulong)Unsafe.SizeOf<MeshRasterPageDescriptor>() + (ulong)dependencies * sizeof(int);
    private static AssetId PageId(AssetId product, int ordinal)
    {
        Span<byte> key = stackalloc byte[20]; product.Value.TryWriteBytes(key[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32LittleEndian(key[16..], ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("SIARASTERPAGESLOT1"u8); hash.AppendData(key);
        var digest = hash.GetHashAndReset(); digest[6] = (byte)((digest[6] & 15) | 128); digest[8] = (byte)((digest[8] & 63) | 128);
        return new(new Guid(digest.AsSpan(0, 16), bigEndian: true));
    }
}
