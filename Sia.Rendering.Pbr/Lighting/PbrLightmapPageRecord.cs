using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrLightmapPageResource")]
public partial record PbrLightmapPageRecord : AssetRecord, ILoadable<PbrLightmapPageRecord>, ILoadable<PbrLightmapPageRecord, ulong>
{
    public required PbrLightmapPageData Data { get; init; }
    public static PbrLightmapPageRecord Load(Stream stream, string? name = null)
        => new() { Data = PbrLightmapPageData.Load(stream), Name = name };
    public static PbrLightmapPageRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = PbrLightmapPageData.Load(stream, maximumBytes), Name = name };
}
