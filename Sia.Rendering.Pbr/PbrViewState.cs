using Sia;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrViewState
{
    internal Dictionary<MeshHandle, GpuMesh> Meshes { get; } = [];

    internal PbrInstanceGpuStore Instances { get; } = new();

    internal CameraUniforms CameraUniforms { get; } = new();

    public LightGpuStore Lights { get; } = new();

    public ClusterGridBuffers ClusterBuffers { get; } = new();

    public ShadowAtlasGpuStore ShadowAtlas { get; } = new();

    public ShadowGpuStore Shadows { get; } = new();

    public IblEnvironmentGpuStore Ibl { get; } = new();

    internal Entity DepthBindGroup { get; set; }

    internal Entity ForwardBindGroup { get; set; }

    internal Entity ForwardLightingBindGroup { get; set; }

    internal Entity CullingBindGroup { get; set; }

    internal Entity[] ShadowCameraBuffers { get; set; } = [];

    internal Entity[] ShadowDrawBindGroups { get; set; } = [];

    internal Entity[] IblPrefilterParamsBuffers { get; set; } = [];

    internal Entity[] IblPrefilterBindGroups { get; set; } = [];

    internal Entity IblBindGroup { get; set; }

    internal bool IblBaked { get; set; }
}
