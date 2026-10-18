using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshVirtualPageAsset")]
public partial record MeshVirtualPageRecord : AssetRecord, ILoadable<MeshVirtualPageRecord>, ILoadable<MeshVirtualPageRecord, ulong>
{
    public required MeshVirtualPageData Data { get; init; }
    public static MeshVirtualPageRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshVirtualPageData.Load(stream), Name = name };
    public static MeshVirtualPageRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshVirtualPageData.Load(stream, maximumBytes), Name = name };
}
