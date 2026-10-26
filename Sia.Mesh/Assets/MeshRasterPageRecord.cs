using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshRasterPageAsset")]
public partial record MeshRasterPageRecord : AssetRecord, ILoadable<MeshRasterPageRecord>, ILoadable<MeshRasterPageRecord, ulong>
{
    public required MeshRasterPageData Data { get; init; }
    public static MeshRasterPageRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshRasterPageData.Load(stream), Name = name };
    public static MeshRasterPageRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshRasterPageData.Load(stream, maximumBytes), Name = name };
}
