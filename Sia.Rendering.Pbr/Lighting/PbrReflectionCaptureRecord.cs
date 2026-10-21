using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrReflectionCaptureResource")]
public partial record PbrReflectionCaptureRecord : AssetRecord,
    ILoadable<PbrReflectionCaptureRecord>, ILoadable<PbrReflectionCaptureRecord, ulong>
{
    public required PbrReflectionCaptureData Data { get; init; }
    public static PbrReflectionCaptureRecord Load(Stream stream, string? name = null)
        => new() { Data = PbrReflectionCaptureData.Load(stream), Name = name };
    public static PbrReflectionCaptureRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = PbrReflectionCaptureData.Load(stream, maximumBytes), Name = name };
}
