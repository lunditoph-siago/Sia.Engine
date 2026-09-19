using Sia;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Unlit;

internal sealed class UnlitViewState
{
    public Dictionary<MeshHandle, GpuMesh> Meshes { get; } = [];

    public Entity CameraBuffer { get; set; }

    public RetainedGpuBuffer<UnlitInstance> Instances { get; } = new();

    public Entity InstanceBuffer => Instances.Buffer;

    public Entity BindGroup { get; set; }

    public ulong InstanceCapacity => Instances.Capacity;

    public ulong? SceneVersion { get; set; }
    public UnlitExtractedView? Extracted { get; set; }
}
