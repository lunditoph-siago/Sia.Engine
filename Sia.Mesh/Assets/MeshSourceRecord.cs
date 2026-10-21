using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshSourceAsset")]
public partial record MeshSourceRecord : AssetRecord, ILoadable<MeshSourceRecord>, ILoadable<MeshSourceRecord, ulong>
{
    public required MeshSource Source { get; init; }

    public static MeshSourceRecord Load(Stream stream, string? name = null)
        => new() { Source = MeshSource.Load(stream), Name = name };

    public static MeshSourceRecord Load(Stream stream, ulong maximumBytes, string? name = null)
        => new() { Source = MeshSource.Load(stream, maximumBytes), Name = name };
}
