using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;

namespace Sia.Engine.Rendering.Pbr;

public sealed class ClusterGridConfig : IAddon
{
    public uint TilesX { get; set; } = 16;
    public uint TilesY { get; set; } = 9;
    public uint ZSlices { get; set; } = 24;
    public uint MaxLightIndicesPerCluster { get; set; } = 64;

    public uint ClusterCount => checked(TilesX * TilesY * ZSlices);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfZero(TilesX);
        ArgumentOutOfRangeException.ThrowIfZero(TilesY);
        ArgumentOutOfRangeException.ThrowIfZero(ZSlices);
        ArgumentOutOfRangeException.ThrowIfZero(MaxLightIndicesPerCluster);
        _ = checked(ClusterCount * MaxLightIndicesPerCluster);
    }
}
