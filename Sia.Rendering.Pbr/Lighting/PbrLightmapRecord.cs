using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrLightmapResource")]
public partial record PbrLightmapRecord : AssetRecord, ILoadable<PbrLightmapRecord>, ILoadable<PbrLightmapRecord, ulong>
{
    public required PbrLightmapData Data { get; init; }
    public static PbrLightmapRecord Load(Stream stream, string? name = null)
        => new() { Data = PbrLightmapData.Load(stream), Name = name };
    public static PbrLightmapRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = PbrLightmapData.Load(stream, maximumBytes), Name = name };
}
