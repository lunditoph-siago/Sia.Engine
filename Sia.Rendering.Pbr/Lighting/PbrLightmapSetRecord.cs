using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrLightmapSetResource")]
public partial record PbrLightmapSetRecord : AssetRecord,ILoadable<PbrLightmapSetRecord>,ILoadable<PbrLightmapSetRecord,ulong>
{
    public required PbrLightmapSetData Data { get; init; }
    public static PbrLightmapSetRecord Load(Stream stream,string? name=null)
        => new() { Data=PbrLightmapSetData.Load(stream),Name=name };
    public static PbrLightmapSetRecord Load(Stream stream,ulong maximumBytes,string? name=null)
        => new() { Data=PbrLightmapSetData.Load(stream,maximumBytes),Name=name };
}
