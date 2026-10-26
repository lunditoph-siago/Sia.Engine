using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterPagesData
{
    /// <summary>
    /// Splits an already built Raster without simplification or vertex remapping. Emits one borrowed leaf at a time;
    /// the caller owns publication, retention and rollback if emission, cancellation or final validation fails.
    /// The byte limit covers index arrays/encoding, not caller-owned Raster, leaf arrays (independently bounded),
    /// dictionaries, list capacity, strings, callback retention or process memory.
    /// Newly derived section bounds use positive zero; original Raster bounds and vertex-channel bits are copied unchanged.
    /// </summary>
    public static MeshRasterPagesData Build(MeshRasterData raster, AssetId rasterId, AssetId productId,
        Action<AssetId, MeshRasterPageData> emitPage, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raster); ArgumentNullException.ThrowIfNull(emitPage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!rasterId.IsValid || !productId.IsValid || rasterId == productId) throw new ArgumentException("Raster and page-index IDs must be distinct and valid.");
        var sectionCount = raster.Sections.Length; var vertexPages = checked((int)(((long)raster.Vertices.Length + MeshRasterPageData.MaximumVertices - 1) / MeshRasterPageData.MaximumVertices));
        long lodCount = 0, pageCount = vertexPages, totalIndices = 0;
        for (var section = 0; section < sectionCount; section++) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var lod in raster.GetLods(section)) {
                lodCount++; totalIndices += lod.IndexCount;
                pageCount += ((long)lod.IndexCount + MeshRasterPageData.MaximumTriangles * 3 - 1) / (MeshRasterPageData.MaximumTriangles * 3);
            }
        }
        if (sectionCount is <= 0 or > MaximumSections || lodCount > MaximumLods || pageCount > MaximumPages || totalIndices > int.MaxValue)
            throw new ArgumentException("Existing Raster exceeds exact page-index count limits.", nameof(raster));
        Admit(sectionCount, (int)lodCount, (int)pageCount, 0, maximumBytes);
        string rasterHash;
        using (var encoded = new HashOnlyStream()) {
            raster.Save(encoded, cancellationToken); rasterHash = encoded.Finish();
            if (encoded.Count != raster.EncodedBytes) throw new InvalidDataException("Raster provenance serialization length mismatch.");
        }
        var sections = new List<MeshRasterPagesSection>(sectionCount); var lods = new List<MeshRasterPagesLod>((int)lodCount);
        var pages = new List<MeshRasterPageDescriptor>((int)pageCount); var dependencies = new List<int>();
        for (var first = 0; first < raster.Vertices.Length;) {
            cancellationToken.ThrowIfCancellationRequested(); var count = System.Math.Min(MeshRasterPageData.MaximumVertices, raster.Vertices.Length - first);
            var page = MeshRasterPageData.CreateVertices(raster.Vertices.Slice(first, count),
                raster.HasBakeUvs ? raster.BakeUvs.Slice(first, count) : default, cancellationToken: cancellationToken);
            var id = PageId(productId, pages.Count);
            pages.Add(new(id, page.ContentHash, page.EncodedBytes, MeshRasterPageKind.Vertices, first, count, 0, 0));
            emitPage(id, page); cancellationToken.ThrowIfCancellationRequested(); first += count;
        }
        for (var section = 0; section < sectionCount; section++) {
            cancellationToken.ThrowIfCancellationRequested(); var source = raster.Sections[section]; var levels = raster.GetLods(section);
            var min = new float3(float.PositiveInfinity); var max = new float3(float.NegativeInfinity); var examined = 0;
            foreach (var index in raster.GetIndices(section)) {
                if ((examined++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var position = raster.Vertices[(int)index].Position; min = math.min(min, position); max = math.max(max, position);
            }
            // SIMD/scalar min/max may choose different signs for an equal zero.
            // Canonicalize only newly derived bounds, never original payloads.
            static float3 PositiveZero(float3 value) => new(value.x == 0 ? 0f : value.x, value.y == 0 ? 0f : value.y, value.z == 0 ? 0f : value.z);
            sections.Add(new(source.MaterialSlot, source.FirstIndex, source.IndexCount, lods.Count, levels.Length, new(PositiveZero(min), PositiveZero(max))));
            for (var level = 0; level < levels.Length; level++) {
                var lod = levels[level]; var indices = raster.GetIndices(section, level); var firstPage = pages.Count;
                for (var at = 0; at < indices.Length;) {
                    cancellationToken.ThrowIfCancellationRequested(); var count = System.Math.Min(MeshRasterPageData.MaximumTriangles * 3, indices.Length - at);
                    var slice = indices.Slice(at, count); var needed = new HashSet<int>(System.Math.Min(count, vertexPages));
                    foreach (var vertex in slice) needed.Add((int)(vertex / MeshRasterPageData.MaximumVertices));
                    var ordered = needed.ToArray(); Array.Sort(ordered);
                    Admit(sectionCount, (int)lodCount, (int)pageCount, checked(dependencies.Count + ordered.Length), maximumBytes);
                    var firstDependency = dependencies.Count; dependencies.AddRange(ordered);
                    var page = MeshRasterPageData.CreateIndices(slice, cancellationToken: cancellationToken); var id = PageId(productId, pages.Count);
                    pages.Add(new(id, page.ContentHash, page.EncodedBytes, MeshRasterPageKind.Indices,
                        lod.FirstIndex + at, count, firstDependency, ordered.Length));
                    emitPage(id, page); cancellationToken.ThrowIfCancellationRequested(); at += count;
                }
                lods.Add(new(lod.FirstIndex, lod.IndexCount, lod.Error, firstPage, pages.Count - firstPage));
            }
        }
        var result = new MeshRasterPagesData(productId, new(rasterId, raster.BuildKey), raster.SourceHash, rasterHash,
            raster.EncodedBytes, raster.Settings, raster.Bounds, raster.Vertices.Length, raster.Indices.Length / 3,
            (int)totalIndices, raster.HasBakeUvs, vertexPages, sections.ToArray(), lods.ToArray(), pages.ToArray(), dependencies.ToArray(), cancellationToken);
        Admit(result._sections.Length, result._lods.Length, result._pages.Length, result._vertexDependencies.Length, maximumBytes);
        return result;
    }

    private static void Admit(int sections, int lods, int pages, int dependencies, ulong maximumBytes)
    {
        if (sections < 0 || sections > MaximumSections || lods < 0 || lods > MaximumLods || pages < 0 || pages > MaximumPages
            || dependencies < 0 || dependencies > MaximumDependencies || WireBytes(sections, lods, pages, dependencies) > MaximumEncodedBytes
            || WireBytes(sections, lods, pages, dependencies) > maximumBytes || OwnedBytes(sections, lods, pages, dependencies) > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Raster index exceeds encoded or known-array admission.");
    }

    private sealed class HashOnlyStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal ulong Count { get; private set; }
        internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); Count = checked(Count + (ulong)buffer.Length); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => checked((long)Count);
        public override long Position { get => checked((long)Count); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
