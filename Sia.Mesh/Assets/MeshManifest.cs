using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>Lightweight source/product inventory; references do not load or pin their payloads.</summary>
public sealed partial class MeshManifest
{
    public const int MaximumMaterialSlots = 4096;
    private readonly int[] _materialSlots;

    public AssetId Source { get; }
    public string SourceHash { get; }
    public Aabb Bounds { get; }
    public int VertexCount { get; }
    public int TriangleCount { get; }
    public bool HasBakeUvs { get; }
    public ReadOnlySpan<int> MaterialSlots => _materialSlots;
    public MeshProductReference<MeshRasterRecord>? Raster { get; }
    public MeshProductReference<MeshBvhRecord>? Bvh { get; }
    public MeshProductReference<MeshVirtualRecord>? Virtual { get; }

    private MeshManifest(AssetId sourceId, string sourceHash, Aabb bounds, int vertexCount,
        int triangleCount, bool hasBakeUvs, int[] materialSlots,
        MeshProductReference<MeshRasterRecord>? raster, MeshProductReference<MeshBvhRecord>? bvh,
        MeshProductReference<MeshVirtualRecord>? virtualGeometry)
    {
        if (!sourceId.IsValid) throw new ArgumentException("Source identity must be nonempty.", nameof(sourceId));
        if (raster?.Id == sourceId || bvh?.Id == sourceId || virtualGeometry?.Id == sourceId
            || (raster is not null && bvh?.Id == raster.Id)
            || (virtualGeometry is not null && (virtualGeometry.Id == raster?.Id || virtualGeometry.Id == bvh?.Id)))
            throw new ArgumentException("Source and independently typed products must use distinct identities.");
        if (sourceHash.Length != 64 || sourceHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Source hash must contain a SHA256 hexadecimal digest.", nameof(sourceHash));
        if (vertexCount <= 0 || triangleCount <= 0)
            throw new ArgumentException("Mesh manifest requires positive source geometry counts.");
        if (!math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max))
            || math.any(bounds.Min > bounds.Max))
            throw new ArgumentException("Mesh manifest bounds must be finite and ordered.", nameof(bounds));
        if (materialSlots.Length is < 1 or > MaximumMaterialSlots)
            throw new ArgumentException("Mesh manifest exceeds its material-slot metadata bound.", nameof(materialSlots));
        var previous = -1;
        foreach (var slot in materialSlots) {
            if (slot <= previous) throw new ArgumentException("Material slots must be nonnegative, sorted and unique.", nameof(materialSlots));
            previous = slot;
        }
        _materialSlots = materialSlots;
        Source = sourceId;
        SourceHash = sourceHash.ToUpperInvariant();
        Bounds = bounds;
        VertexCount = vertexCount;
        TriangleCount = triangleCount;
        HasBakeUvs = hasBakeUvs;
        Raster = raster;
        Bvh = bvh;
        Virtual = virtualGeometry;
    }

    public static MeshManifest Create(AssetId sourceId, MeshSource source,
        MeshProductReference<MeshRasterRecord>? raster = null, MeshProductReference<MeshBvhRecord>? bvh = null,
        MeshProductReference<MeshVirtualRecord>? virtualGeometry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var slots = new HashSet<int>();
        foreach (var section in source.Sections) {
            if (slots.Add(section.MaterialSlot) && slots.Count > MaximumMaterialSlots)
                throw new ArgumentException("Mesh manifest exceeds its material-slot metadata bound.", nameof(source));
        }
        var materialSlots = slots.ToArray();
        Array.Sort(materialSlots);
        return new(sourceId, source.ContentHash, source.Bounds, source.Vertices.Length,
            source.Indices.Length / 3, source.HasBakeUvs, materialSlots, raster, bvh, virtualGeometry);
    }
}
