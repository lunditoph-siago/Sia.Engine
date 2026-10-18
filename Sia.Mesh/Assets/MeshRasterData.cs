using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterData
{
    private readonly MeshVertex[] _vertices;
    private readonly uint[] _indices;
    private readonly MeshSection[] _sections;
    private readonly float2[] _bakeUvs;
    private readonly MeshRasterLod[] _lods;
    private readonly int[] _lodOffsets;
    private readonly int _sourceIndexCount;

    public ReadOnlySpan<MeshVertex> Vertices => _vertices;
    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, _sourceIndexCount);
    public ReadOnlySpan<MeshSection> Sections => _sections;
    public ReadOnlySpan<float2> BakeUvs => _bakeUvs;
    public bool HasBakeUvs => _bakeUvs.Length != 0;
    public Aabb Bounds { get; }
    public string SourceHash { get; }
    public string BuildKey { get; }
    public MeshRasterBuildSettings Settings { get; }
    public ulong PayloadBytes => OwnedBytes(_vertices.Length, _indices.Length, _sections.Length, _lods.Length, HasBakeUvs);
    public ulong EncodedBytes => 169ul + (ulong)_vertices.Length * (HasBakeUvs ? 56ul : 48ul) + (ulong)_indices.Length * 4
        + (ulong)_sections.Length * 12 + (ulong)_lods.Length * 12 + (ulong)_lodOffsets.Length * 4;

    private MeshRasterData(MeshVertex[] vertices, uint[] indices, MeshSection[] sections, float2[] bakeUvs,
        int sourceIndexCount, MeshRasterLod[] lods, int[] offsets, Aabb bounds, string sourceHash, MeshRasterBuildSettings settings)
    {
        _vertices = vertices; _indices = indices; _sections = sections; _bakeUvs = bakeUvs;
        _sourceIndexCount = sourceIndexCount; _lods = lods; _lodOffsets = offsets;
        Bounds = bounds; SourceHash = sourceHash; Settings = settings; BuildKey = ComputeBuildKey(sourceHash, settings);
        ValidateLods();
    }

    public ReadOnlySpan<MeshRasterLod> GetLods(int section)
    {
        if ((uint)section >= _sections.Length) throw new ArgumentOutOfRangeException(nameof(section));
        return _lods.AsSpan(_lodOffsets[section], _lodOffsets[section + 1] - _lodOffsets[section]);
    }

    public ReadOnlySpan<uint> GetIndices(int section, int lod = 0)
    {
        var levels = GetLods(section);
        if ((uint)lod >= levels.Length) throw new ArgumentOutOfRangeException(nameof(lod));
        var range = levels[lod]; return _indices.AsSpan(range.FirstIndex, range.IndexCount);
    }

    internal static string ComputeBuildKey(string sourceHash, MeshRasterBuildSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIARASTERLOD1"u8); hash.AppendData(Convert.FromHexString(sourceHash));
        Span<byte> bytes = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, settings.MaximumLods);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], settings.MinimumTriangles);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], settings.TriangleRatio);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], settings.NormalWeight);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], settings.UVWeight);
        hash.AppendData(bytes); return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static MeshRasterData Build(MeshSource source, MeshRasterBuildSettings? settings = null,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = settings ?? MeshRasterBuildSettings.Default;
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        cancellationToken.ThrowIfCancellationRequested();
        if (OwnedBytes(source.Vertices.Length, source.Indices.Length, source.Sections.Length, source.Sections.Length, source.HasBakeUvs) > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var indices = new List<uint>(source.Indices.ToArray()); var levels = new List<MeshRasterLod>();
        var offsets = new int[source.Sections.Length + 1];
        for (var section = 0; section < source.Sections.Length; section++) {
            cancellationToken.ThrowIfCancellationRequested();
            offsets[section] = levels.Count; var part = source.Sections[section];
            levels.Add(new(part.FirstIndex, part.IndexCount, 0));
            if (options.MaximumLods == 1 || part.IndexCount / 3 <= options.MinimumTriangles) continue;
            var inputIndices = source.Indices.Slice(part.FirstIndex, part.IndexCount);
            var geometry = MeshPatchBuilder.Compact(source.Vertices, inputIndices, source.BakeUvs, maximumBytes, cancellationToken);
            var original = new Dictionary<MeshVertexIdentity, uint>();
            foreach (var index in inputIndices) original.TryAdd(MeshVertexIdentity.Create(source.Vertices[(int)index],
                source.HasBakeUvs ? source.BakeUvs[(int)index] : default), index);
            var error = 0f;
            for (var lod = 1; lod < options.MaximumLods; lod++) {
                var count = geometry.Indices.Length / 3;
                if (count <= options.MinimumTriangles) break;
                var target = System.Math.Max(options.MinimumTriangles, (int)System.Math.Ceiling(count * (double)options.TriangleRatio));
                var reduced = MeshSimplifier.Reduce(geometry, target, options.NormalWeight, options.UVWeight, cancellationToken, false);
                if (reduced.Geometry.Indices.Length >= geometry.Indices.Length) break;
                geometry = reduced.Geometry; error += reduced.Error;
                if (!float.IsFinite(error)) throw new ArgumentException("Raster LOD error exceeds finite precision.", nameof(source));
                if (error != 0) error = MathF.BitIncrement(error);
                if (OwnedBytes(source.Vertices.Length, checked(indices.Count + geometry.Indices.Length), source.Sections.Length,
                        levels.Count + 1 + source.Sections.Length - section - 1, source.HasBakeUvs) > maximumBytes)
                    throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                levels.Add(new(indices.Count, geometry.Indices.Length, error));
                foreach (var index in geometry.Indices) indices.Add(original[MeshVertexIdentity.Create(geometry.Vertices[(int)index],
                    geometry.HasBakeUvs ? geometry.BakeUvs[(int)index] : default)]);
            }
        }
        offsets[^1] = levels.Count;
        var result = new MeshRasterData(source.Vertices.ToArray(), indices.ToArray(), source.Sections.ToArray(), source.BakeUvs.ToArray(),
            source.Indices.Length, levels.ToArray(), offsets, source.Bounds, source.ContentHash, options);
        if (result.EncodedBytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        return result;
    }

    private void ValidateLods()
    {
        if (!Settings.IsValid || _lodOffsets.Length != _sections.Length + 1 || _lodOffsets[0] != 0 || _lodOffsets[^1] != _lods.Length)
            throw new ArgumentException("Invalid Raster LOD settings or offsets.");
        var cursor = _sourceIndexCount;
        for (var section = 0; section < _sections.Length; section++) {
            var start = _lodOffsets[section]; var end = _lodOffsets[section + 1]; var part = _sections[section];
            if (start < 0 || end <= start || end > _lods.Length || end - start > Settings.MaximumLods
                || _lods[start] != new MeshRasterLod(part.FirstIndex, part.IndexCount, 0))
                throw new ArgumentException("Raster LOD0 must match its source section.");
            var vertices = new HashSet<uint>(System.Math.Min(_vertices.Length, part.IndexCount));
            foreach (var index in Indices.Slice(part.FirstIndex, part.IndexCount)) vertices.Add(index);
            for (var at = start + 1; at < end; at++) {
                var level = _lods[at]; var previous = _lods[at - 1];
                if (level.FirstIndex != cursor || level.IndexCount <= 0 || level.IndexCount % 3 != 0 || level.IndexCount >= previous.IndexCount
                    || !float.IsFinite(level.Error) || level.Error < previous.Error || level.FirstIndex > _indices.Length - level.IndexCount)
                    throw new ArgumentException("Raster LODs require decreasing counts, ordered errors and disjoint complete ranges.");
                foreach (var index in _indices.AsSpan(level.FirstIndex, level.IndexCount))
                    if (!vertices.Contains(index)) throw new ArgumentException("Raster LOD crosses a source section boundary.");
                cursor = checked(cursor + level.IndexCount);
            }
        }
        if (cursor != _indices.Length) throw new ArgumentException("Unreferenced Raster LOD topology.");
    }

    private static ulong OwnedBytes(int vertices, int indices, int sections, int lods, bool bake)
        => (ulong)vertices * (ulong)(MeshVertex.Stride + (bake ? System.Runtime.CompilerServices.Unsafe.SizeOf<float2>() : 0)) + (ulong)indices * 4
            + (ulong)sections * 12 + (ulong)lods * 12 + (ulong)(sections + 1L) * 4;

    public MeshData CopyGeometry() => new([.. _vertices], Indices.ToArray(), Bounds) { BakeUvs = [.. _bakeUvs] };
}
