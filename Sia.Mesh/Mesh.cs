using Sia.Asset;

namespace Sia.Engine.Mesh;

/// <summary>Scene reference; the rendering use site selects and resolves an available product.</summary>
public record struct Mesh(AssetRefer<MeshManifestRecord> Asset);
