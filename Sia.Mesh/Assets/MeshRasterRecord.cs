using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshRasterAsset")]
public partial record MeshRasterRecord : AssetRecord, ILoadable<MeshRasterRecord>, ILoadable<MeshRasterRecord, ulong>
{
    public required MeshRasterData Data { get; init; }

    public static MeshRasterRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshRasterData.Load(stream), Name = name };

    public static MeshRasterRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshRasterData.Load(stream, maximumBytes), Name = name };
}
