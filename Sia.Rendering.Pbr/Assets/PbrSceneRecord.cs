using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrSceneResource")]
public partial record PbrSceneRecord : AssetRecord, ILoadable<PbrSceneRecord>, ILoadable<PbrSceneRecord, ulong>
{
    public required PbrSceneData Data { get; init; }
    public static PbrSceneRecord Load(Stream stream, string? name = null) => new() { Data = PbrSceneData.Load(stream), Name = name };
    public static PbrSceneRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = PbrSceneData.Load(stream, maximumBytes), Name = name };
}
