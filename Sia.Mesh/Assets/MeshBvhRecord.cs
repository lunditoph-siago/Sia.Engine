using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshBvhAsset")]
public partial record MeshBvhRecord : AssetRecord, ILoadable<MeshBvhRecord>, ILoadable<MeshBvhRecord, ulong>
{
    public required MeshBvhData Data { get; init; }

    public static MeshBvhRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshBvhData.Load(stream), Name = name };

    public static MeshBvhRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Data = MeshBvhData.Load(stream, maximumBytes), Name = name };
}
