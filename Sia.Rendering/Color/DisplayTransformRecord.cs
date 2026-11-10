using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering;

[SiaAsset]
[SiaTemplate("DisplayTransformResource")]
public partial record DisplayTransformRecord : AssetRecord, ILoadable<DisplayTransformRecord>
{
    public required DisplayTransformData Data { get; init; }

    public static DisplayTransformRecord Load(Stream stream, string? name = null)
        => new() { Data = DisplayTransformData.Read(stream), Name = name };
}
