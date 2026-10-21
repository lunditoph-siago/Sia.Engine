using Sia;
using Sia.Asset;

namespace Sia.Engine.Mesh;

[SiaAsset]
[SiaTemplate("MeshManifestAsset")]
public partial record MeshManifestRecord : AssetRecord, ILoadable<MeshManifestRecord>
{
    public required MeshManifest Data { get; init; }

    public static MeshManifestRecord Load(Stream stream, string? name = null)
        => new() { Data = MeshManifest.Load(stream), Name = name };
}
