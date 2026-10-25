namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    internal sealed record TextureInfo(uint Width, uint Height, int Levels, int TailMip);
    internal Dictionary<PbrTextureData, TextureInfo> TextureSources { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PbrTextureData, PbrTextureData> _fullTextures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PbrTextureData, PbrTextureMipsData> _textureMips = new(ReferenceEqualityComparer.Instance);
    private readonly Func<PbrTextureData, PbrTextureMipsData?>? _findTextureMips;
    private readonly Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? _prepareMip;
    private static PbrTextureData?[] TextureMaps(PbrMaterialAsset material)
        => [material.BaseColor, material.Normal, material.MetallicRoughness, material.Occlusion, material.Emissive];
    private static PbrMaterialAsset WithTextures(PbrMaterialAsset material, PbrTextureData?[] maps)
        => material with { BaseColor = maps[0], Normal = maps[1], MetallicRoughness = maps[2], Occlusion = maps[3], Emissive = maps[4] };

    // GPU upload views borrow already acquired independent textures; these are not new asset identities.
    private PbrMaterialAsset[] InitializeTextures(ReadOnlySpan<PbrMaterialAsset> materials)
    {
        var tails = new Dictionary<PbrTextureData, PbrTextureData>(ReferenceEqualityComparer.Instance);
        var result = new PbrMaterialAsset[materials.Length];
        for (var i = 0; i < materials.Length; i++) {
            materials[i].DiffuseMoments?.ValidateSources(materials[i].BaseColor, materials[i].MetallicRoughness);
            var maps = TextureMaps(materials[i]);
            for (var m = 0; m < maps.Length; m++) {
                if (maps[m] is not { } source) continue;
                if (!tails.TryGetValue(source, out var tail)) {
                    var index = _findTextureMips?.Invoke(source);
                    if (index is not null && !ReferenceEquals(index.Tail, source))
                        throw new InvalidDataException("Texture index must describe the acquired bootstrap tail.");
                    var first = 0;
                    while (first + 1 < source.MipLevels.Length && System.Math.Max(source.Width >> first, source.Height >> first) > 64) first++;
                    tail = source.SliceMipChain(first);
                    tails.Add(source, tail); _fullTextures.Add(tail, source);
                    if (index is not null) _textureMips.Add(tail, index);
                    TextureSources.Add(tail, index is null ? new(source.Width, source.Height, source.MipLevels.Length, first)
                        : new(index.Width, index.Height, index.Levels, index.TailMip + first));
                }
                maps[m] = tail;
            }
            result[i] = WithTextures(materials[i], maps);
        }
        return result;
    }

    internal async Task<ReadOnlyMemory<byte>[]> ReadTextureTailAsync(PbrTextureData source, int firstMip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var full = _fullTextures[source];
        if (firstMip < 0 || firstMip > TextureSources[source].TailMip) throw new ArgumentOutOfRangeException(nameof(firstMip));
        if (!_textureMips.TryGetValue(source, out var index)) return full.MipLevels.Span[firstMip..].ToArray();
        var result = new ReadOnlyMemory<byte>[index.Levels - firstMip];
        for (var mip = firstMip; mip < index.TailMip; mip++) {
            var record = await _prepareMip!(index.Mips[mip], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            index.ValidateMip(mip, record);
            result[mip - firstMip] = record.Data.MipLevels.Span[0];
        }
        var firstTail = System.Math.Max(firstMip, index.TailMip);
        full.MipLevels.Span[(firstTail - index.TailMip)..].CopyTo(result.AsSpan(firstTail - firstMip));
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal async Task<ReadOnlyMemory<byte>> ReadTextureMipAsync(PbrTextureData source, int mip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = TextureSources[source];
        if ((uint)mip >= (uint)info.TailMip) throw new ArgumentOutOfRangeException(nameof(mip));
        if (!_textureMips.TryGetValue(source, out var index)) return _fullTextures[source].MipLevels.Span[mip];
        var record = await _prepareMip!(index.Mips[mip], token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        index.ValidateMip(mip, record);
        return record.Data.MipLevels.Span[0];
    }
}
