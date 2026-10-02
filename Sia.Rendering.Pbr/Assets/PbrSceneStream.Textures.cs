using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    private const int k_MipChunkBytes = 128 * 1024;

    internal sealed record TextureInfo(
        uint Width,
        uint Height,
        int Levels,
        int TailMip,
        string[][] FineMips);

    internal Dictionary<PbrTextureData, TextureInfo> TextureSources { get; } = [with(ReferenceEqualityComparer.Instance)];

    private static PbrTextureData?[] TextureMaps(PbrMaterialAsset m)
        => [m.BaseColor, m.Normal, m.MetallicRoughness, m.Occlusion, m.Emissive];

    private static PbrMaterialAsset WithTextures(PbrMaterialAsset m, PbrTextureData?[] maps)
        => m with { BaseColor = maps[0], Normal = maps[1], MetallicRoughness = maps[2], Occlusion = maps[3], Emissive = maps[4] };

    private static async Task<(PbrMaterialAsset[] Materials, TextureInfo[] Textures, int[][] Maps)> CookTexturesAsync(
        PbrMaterialAsset[] materials, Func<byte[], Task<string>> write, CancellationToken token)
    {
        var sources = materials.SelectMany(TextureMaps).OfType<PbrTextureData>()
            .Distinct<PbrTextureData>(ReferenceEqualityComparer.Instance).ToArray();
        var indices = new Dictionary<PbrTextureData, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < sources.Length; i++) indices.Add(sources[i], i);
        var tails = new PbrTextureData[sources.Length];
        var infos = new TextureInfo[sources.Length];
        for (var i = 0; i < sources.Length; i++) {
            token.ThrowIfCancellationRequested();
            var t = sources[i];
            var tail = 0;
            while (tail + 1 < t.MipLevels.Length && System.Math.Max(t.Width >> tail, t.Height >> tail) > 64) tail++;
            var mips = new string[tail][];
            for (var mip = 0; mip < tail; mip++) {
                var level = t.MipLevels.Span[mip];
                var parts = new List<string>();
                for (var offset = 0; offset < level.Length; offset += k_MipChunkBytes) {
                    token.ThrowIfCancellationRequested();
                    parts.Add(await write(SceneStreamBlock.Encode(level.Span.Slice(offset, System.Math.Min(k_MipChunkBytes, level.Length - offset)), System.IO.Compression.CompressionLevel.Optimal)));
                }
                mips[mip] = [.. parts];
            }
            tails[i] = PbrTextureData.Create(System.Math.Max(1u, t.Width >> tail), System.Math.Max(1u, t.Height >> tail),
                t.Srgb, t.MipLevels.Span[tail..], t.Sampler);
            infos[i] = new(t.Width, t.Height, t.MipLevels.Length, tail, mips);
        }
        var maps = materials.Select(m => TextureMaps(m).Select(t => t is null ? -1 : indices[t]).ToArray()).ToArray();
        var result = materials.Select((m, i) => WithTextures(m, [.. maps[i].Select(id => id < 0 ? null : tails[id])])).ToArray();
        return (result, infos, maps);
    }

    private static void ValidateTextures(Header h, AssetChunkManifest manifest)
    {
        if (h.Textures is null || h.Textures.Length > 1280 || h.TextureMaps is null || h.TextureMaps.Length > 256)
            throw new InvalidDataException("Missing or oversized texture stream metadata; recook older assets.");
        foreach (var t in h.Textures) {
            if (t is null || t.Width is 0 or > 8192 || t.Height is 0 or > 8192 || t.Levels is < 1 or > 14
                || t.TailMip < 0 || t.TailMip >= t.Levels || t.FineMips is null || t.FineMips.Length != t.TailMip
                || (t.Levels > 1 && (t.Width >> (t.Levels - 2)) <= 1 && (t.Height >> (t.Levels - 2)) <= 1))
                throw new InvalidDataException("Invalid streamed texture shape.");
            for (var mip = 0; mip < t.TailMip; mip++) {
                var bytes = checked((long)System.Math.Max(1u, t.Width >> mip) * System.Math.Max(1u, t.Height >> mip) * 4);
                var ids = t.FineMips[mip];
                if (ids is null || ids.Length != (bytes + k_MipChunkBytes - 1) / k_MipChunkBytes)
                    throw new InvalidDataException("Invalid texture mip reservation.");
                foreach (var id in ids)
                    if (string.IsNullOrEmpty(id) || manifest.GetChunk(id).Length > SceneStreamBlock.MaximumEncodedLength(k_MipChunkBytes))
                        throw new InvalidDataException("Invalid texture mip chunk.");
            }
        }
        foreach (var maps in h.TextureMaps)
            if (maps is not { Length: 5 } || maps.Any(id => id < -1 || id >= h.Textures.Length))
                throw new InvalidDataException("Invalid material texture references.");
    }

    private void InitializeTextures(Header h, PbrSceneAsset bootstrap)
    {
        if (h.TextureMaps.Length != bootstrap.Materials.Length)
            throw new InvalidDataException("Material texture table length mismatch.");
        var seen = new HashSet<int>();
        for (var material = 0; material < h.TextureMaps.Length; material++) {
            var maps = TextureMaps(bootstrap.Materials.Span[material]);
            for (var map = 0; map < 5; map++) {
                var id = h.TextureMaps[material][map];
                var texture = maps[map];
                if ((id < 0) != (texture is null))
                    throw new InvalidDataException("Bootstrap texture reference mismatch.");
                if (texture is null) continue;
                var info = h.Textures[id];
                if (texture.Width != System.Math.Max(1u, info.Width >> info.TailMip)
                    || texture.Height != System.Math.Max(1u, info.Height >> info.TailMip)
                    || texture.MipLevels.Length != info.Levels - info.TailMip)
                    throw new InvalidDataException("Bootstrap texture tail mismatch.");
                if (TextureSources.TryGetValue(texture, out var prior) && prior != info)
                    throw new InvalidDataException("Aliased texture descriptors disagree.");
                TextureSources[texture] = info;
                seen.Add(id);
            }
        }
        if (seen.Count != h.Textures.Length) throw new InvalidDataException("Unused texture descriptors.");
    }

    internal async Task<ReadOnlyMemory<byte>[]> ReadTextureTailAsync(PbrTextureData source, int firstMip, CancellationToken token)
    {
        var info = TextureSources[source];
        if (firstMip < 0 || firstMip > info.TailMip) throw new ArgumentOutOfRangeException(nameof(firstMip));
        var levels = new ReadOnlyMemory<byte>[info.Levels - firstMip];
        for (var mip = firstMip; mip < info.Levels; mip++) {
            token.ThrowIfCancellationRequested();
            if (mip >= info.TailMip) { levels[mip - firstMip] = source.MipLevels.Span[mip - info.TailMip]; continue; }
            var bytes = new byte[checked((int)(System.Math.Max(1u, info.Width >> mip) * System.Math.Max(1u, info.Height >> mip) * 4))];
            var offset = 0;
            foreach (var id in info.FineMips[mip]) {
                using var lease = await _cache.AcquireAsync(_manifest.GetChunk(id), cancellationToken: token).ConfigureAwait(false);
                var expected = System.Math.Min(k_MipChunkBytes, bytes.Length - offset);
                var count = SceneStreamBlock.Decode(lease.Memory, bytes.AsSpan(offset, expected));
                if (count != expected) throw new InvalidDataException("Texture mip payload length mismatch.");
                offset += count;
            }
            levels[mip - firstMip] = bytes;
        }
        return levels;
    }

    /// <summary>Loads original material mip chains for offline consumers, within an explicit decoded byte limit.</summary>
    public async Task<PbrMaterialAsset[]> LoadFullMaterialsAsync(long maximumBytes = 256L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        foreach (var t in TextureSources.Values)
            for (var mip = 0; mip < t.Levels; mip++) bytes = checked(bytes + ((long)System.Math.Max(1u, t.Width >> mip) * System.Math.Max(1u, t.Height >> mip) * 4));
        if (maximumBytes < 0 || bytes > maximumBytes)
            throw new InvalidOperationException("Offline texture decode budget exceeded.");
        var full = new Dictionary<PbrTextureData, PbrTextureData>(ReferenceEqualityComparer.Instance);
        foreach (var pair in TextureSources) {
            var levels = await ReadTextureTailAsync(pair.Key, 0, cancellationToken).ConfigureAwait(false);
            full[pair.Key] = PbrTextureData.FromValidatedMipChain(pair.Value.Width, pair.Value.Height, pair.Key.Srgb, pair.Key.Sampler, levels);
        }
        return [.. Bootstrap.Materials.ToArray().Select(m => WithTextures(m, [.. TextureMaps(m).Select(t => t is null ? null : full[t])]))];
    }
}
