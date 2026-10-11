using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>One original material section, with mesh-local independently cooked patch geometry.</summary>
public readonly record struct MeshVirtualPart(int MaterialSlot, int FirstSourceTriangle, MeshPatchAsset Patches);

/// <summary>Transient virtual derivation result. Persistent records use an independent index and typed pages.</summary>
public sealed class MeshVirtualData
{
    public const int MaximumParts = 4096;
    private readonly MeshVirtualPart[] _parts;
    public ReadOnlySpan<MeshVirtualPart> Parts => _parts;
    public string SourceHash { get; }
    public string BuildKey { get; }
    public MeshPatchBuildSettings Settings { get; }
    public Aabb Bounds { get; }
    public int SourceVertexCount { get; }
    public int SourceTriangleCount { get; }
    public bool HasBakeUvs { get; }
    public int FinestTriangleCount { get; }
    public ulong PayloadBytes { get; }

    private MeshVirtualData(string sourceHash, MeshPatchBuildSettings settings, Aabb bounds, int vertices,
        int triangles, bool bakeUvs, MeshVirtualPart[] parts)
    {
        if (!settings.IsValid || vertices <= 0 || triangles <= 0 || parts.Length is < 1 or > MaximumParts
            || !math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)) || math.any(bounds.Min > bounds.Max))
            throw new ArgumentException("Invalid virtual Mesh summary or build settings.");
        if (sourceHash.Length != 64 || sourceHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Virtual Mesh requires a SHA256 source digest.", nameof(sourceHash));
        SourceHash = sourceHash.ToUpperInvariant();
        Settings = settings; Bounds = bounds;
        SourceVertexCount = vertices; SourceTriangleCount = triangles; HasBakeUvs = bakeUvs;
        _parts = parts;
        long sourceTriangles = 0;
        var finest = 0;
        var owned = PartArrayBytes(parts.Length);
        foreach (var part in parts) {
            if (part.MaterialSlot < 0 || part.FirstSourceTriangle != sourceTriangles || part.Patches is null
                || part.Patches.Settings != settings || part.Patches.Build.SourceTriangleCount <= 0)
                throw new ArgumentException("Virtual Mesh parts must preserve ordered source sections and settings.");
            var tree = part.Patches.Build.Tree;
            if (tree.VertexCount > vertices || (tree.VertexCount != 0 && (tree.HasBakeUvs != bakeUvs || math.any(tree.Bounds.Min < bounds.Min)
                || math.any(tree.Bounds.Max > bounds.Max))))
                throw new ArgumentException("Virtual Mesh part attributes/bounds differ from the source summary.");
            sourceTriangles += part.Patches.Build.SourceTriangleCount;
            finest = checked(finest + tree.FinestTriangleCount);
            owned += tree.PayloadBytes;
        }
        if (sourceTriangles != triangles) throw new ArgumentException("Virtual Mesh parts do not cover the source triangles.");
        FinestTriangleCount = finest; PayloadBytes = owned;
        BuildKey = ComputeBuildKey(SourceHash, settings);
    }

    internal static string ComputeBuildKey(string sourceHash, MeshPatchBuildSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHVIRTUAL1"u8);
        hash.AppendData(Convert.FromHexString(sourceHash));
        Span<byte> bytes = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, settings.MaxLeafTriangles);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], settings.MaxChildren);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], settings.ParentTriangleRatio);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], settings.NormalWeight);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], settings.UVWeight);
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static ulong PartArrayBytes(int count) => (ulong)count * (ulong)Unsafe.SizeOf<MeshVirtualPart>();

    public static MeshVirtualData Build(MeshSource source, MeshPatchBuildSettings? settings = null,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = settings ?? MeshPatchBuildSettings.Default;
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        if (source.Sections.Length > MaximumParts) throw new ArgumentException("Virtual Mesh exceeds its material-section bound.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        var owned = PartArrayBytes(source.Sections.Length);
        if (owned > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var parts = new MeshVirtualPart[source.Sections.Length];
        for (var i = 0; i < parts.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var section = source.Sections[i];
            var compact = MeshPatchBuilder.Compact(source.Vertices,
                source.Indices.Slice(section.FirstIndex, section.IndexCount), source.BakeUvs, maximumBytes - owned, cancellationToken);
            var patches = MeshPatchAsset.Cook(compact, options, cancellationToken);
            if (patches.Build.Tree.PayloadBytes > maximumBytes - owned)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Virtual Mesh exceeds its owned-array budget.");
            owned += patches.Build.Tree.PayloadBytes;
            parts[i] = new(section.MaterialSlot, section.FirstIndex / 3, patches);
        }
        return new(source.ContentHash, options, source.Bounds, source.Vertices.Length,
            source.Indices.Length / 3, source.HasBakeUvs, parts);
    }
}
