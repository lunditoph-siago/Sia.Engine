using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Explicit acquired live diffuse-transport input by material asset ID; does not replace the frozen bake or draw material.</summary>
public readonly record struct PbrTransportMaterial(AssetId Id, PbrMaterialAsset Material);
