using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrTextureAsset")]
public partial record PbrTextureRecord : AssetRecord, ILoadable<PbrTextureRecord>, ILoadable<PbrTextureRecord, ulong>
{
    public required PbrTextureData Data { get; init; }
    public static PbrTextureRecord Load(Stream stream, string? name = null) => new() { Data = PbrTextureData.Load(stream), Name = name };
    public static PbrTextureRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = PbrTextureData.Load(stream, maximumBytes), Name = name };
}
