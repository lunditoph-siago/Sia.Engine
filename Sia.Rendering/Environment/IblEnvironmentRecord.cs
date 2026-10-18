using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering;

[SiaAsset]
[SiaTemplate("IblEnvironmentResource")]
public partial record IblEnvironmentRecord : AssetRecord, ILoadable<IblEnvironmentRecord>
{
    public required IblEnvironmentAsset Data { get; init; }

    public static IblEnvironmentRecord Load(Stream stream, string? name = null)
        => new() { Data = IblEnvironmentAsset.Read(stream), Name = name };
}
