using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshVirtualAsset")]
public partial record MeshVirtualRecord : AssetRecord, ILoadable<MeshVirtualRecord>, ILoadable<MeshVirtualRecord, ulong>
{
    public required MeshVirtualPagesData Data { get; init; }
    public static MeshVirtualRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshVirtualPagesData.Load(stream), Name = name };
    public static MeshVirtualRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshVirtualPagesData.Load(stream, maximumBytes), Name = name };
}
