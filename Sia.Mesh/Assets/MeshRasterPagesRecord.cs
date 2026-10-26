using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshRasterPagesAsset")]
public partial record MeshRasterPagesRecord : AssetRecord, ILoadable<MeshRasterPagesRecord>, ILoadable<MeshRasterPagesRecord, ulong>
{
    public required MeshRasterPagesData Data { get; init; }
    public static MeshRasterPagesRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshRasterPagesData.Load(stream), Name = name };
    public static MeshRasterPagesRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshRasterPagesData.Load(stream, maximumBytes), Name = name };
}
