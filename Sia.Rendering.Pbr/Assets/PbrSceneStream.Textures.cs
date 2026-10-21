namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    internal sealed record TextureInfo(uint Width, uint Height, int Levels, int TailMip);
    internal Dictionary<PbrTextureData, TextureInfo> TextureSources { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PbrTextureData, PbrTextureData> _fullTextures = new(ReferenceEqualityComparer.Instance);
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
            var maps = TextureMaps(materials[i]);
            for (var m = 0; m < maps.Length; m++) {
                if (maps[m] is not { } source) continue;
                if (!tails.TryGetValue(source, out var tail)) {
                    var first = 0;
                    while (first + 1 < source.MipLevels.Length && System.Math.Max(source.Width >> first, source.Height >> first) > 64) first++;
                    tail = PbrTextureData.FromValidatedMipChain(System.Math.Max(1u, source.Width >> first), System.Math.Max(1u, source.Height >> first),
                        source.Srgb, source.Sampler, source.MipLevels.Span[first..].ToArray());
                    tails.Add(source, tail); _fullTextures.Add(tail, source);
                    TextureSources.Add(tail, new(source.Width, source.Height, source.MipLevels.Length, first));
                }
                maps[m] = tail;
            }
            result[i] = WithTextures(materials[i], maps);
        }
        return result;
    }

    internal Task<ReadOnlyMemory<byte>[]> ReadTextureTailAsync(PbrTextureData source, int firstMip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var full = _fullTextures[source];
        if (firstMip < 0 || firstMip > TextureSources[source].TailMip) throw new ArgumentOutOfRangeException(nameof(firstMip));
        return Task.FromResult(full.MipLevels.Span[firstMip..].ToArray());
    }
}
