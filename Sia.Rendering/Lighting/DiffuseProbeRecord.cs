using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering;

[SiaAsset]
[SiaTemplate("DiffuseProbeResource")]
public partial record DiffuseProbeRecord : AssetRecord, ILoadable<DiffuseProbeRecord>
{
    public required DiffuseProbeAsset Data { get; init; }

    public static DiffuseProbeRecord Load(Stream stream, string? name = null)
        => new() { Data = DiffuseProbeAsset.Read(stream), Name = name };
}
