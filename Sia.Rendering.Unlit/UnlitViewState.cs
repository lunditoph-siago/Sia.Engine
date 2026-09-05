using Sia;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Unlit;

internal sealed class UnlitViewState
{
    public Dictionary<MeshHandle, GpuMesh> Meshes { get; } = [];

    public Entity CameraBuffer { get; set; }

    public Entity InstanceBuffer { get; set; }

    public Entity BindGroup { get; set; }

    public ulong InstanceCapacity { get; set; }
}
