using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrMaterialResource")]
public partial record PbrMaterialRecord : AssetRecord, ILoadable<PbrMaterialRecord>
{
    public required PbrMaterialData Data { get; init; }
    public static PbrMaterialRecord Load(Stream stream, string? name = null) => new() { Data = PbrMaterialData.Load(stream), Name = name };
}
