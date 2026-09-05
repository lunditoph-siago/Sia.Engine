using Sia;
using Sia.Engine.Mesh;
using Sia.WebGPU;

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

    internal ProceduralSky? PreparedSky { get; set; }

    internal AtmosphereGpuState? Atmosphere { get; set; }
    internal SkyAtmosphere? ActiveAtmosphere { get; set; }

    internal ulong EnvironmentRevision { get; set; }

    internal Entity SkyboxUniforms { get; set; }
    internal Entity SkyboxBindGroup { get; set; }
    internal Entity ToneMappingUniforms { get; set; }
    internal Entity ToneMappingBindGroup { get; set; }
    internal WgpuHandle<WGPUTextureView> ToneMappingSource { get; set; }
}
