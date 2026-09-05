using Sia;
using Sia.Engine.Camera;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class AtmosphereGpuState
{
    private readonly AtmospherePipelines _pipelines;
    private readonly Entity _uniform;
    private readonly Entity _sampler;
    private readonly Entity[] _textures;
    private readonly Entity[] _views;
    private readonly Entity[] _commonGroups;
    private readonly Entity[] _outputGroups;
    private readonly Entity[] _prefilterGroups;
    private SkyAtmosphere? _prepared;
    private AtmosphereUniformData _data;
    private ulong _mediumRevision;
    private ulong _environmentRevision;
    private ulong _viewRevision;
    private readonly ulong[] _renderedRevisions = new ulong[5];
    private Entity _compositeGroup;
    private WgpuHandle<WGPUTextureView> _compositeSource;
    private WgpuHandle<WGPUTextureView> _compositeDepth;

    public bool Prepare(in GpuFrame frame, SkyAtmosphere sky, in CameraMatrices camera, bool forceEnvironment)
    {
        var data = AtmosphereUniformData.From(sky, camera);
        var mediumChanged = _prepared is null || !SameMedium(_prepared, sky);
        var environmentChanged = forceEnvironment || mediumChanged || !_data.Sun.Equals(data.Sun)
            || _data.MieAbsorption.w != data.MieAbsorption.w
            || !_data.Irradiance.Equals(data.Irradiance) || !_data.CameraPlanet.Equals(data.CameraPlanet);
        var viewChanged = environmentChanged || !_data.Equals(data);
        if (mediumChanged) { _mediumRevision++; }
        if (environmentChanged) { _environmentRevision++; }
        if (viewChanged) {
            _viewRevision++;
            Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _uniform.GetWgpu<WGPUBuffer>(), 0, [data]);
        }
        _prepared = sky;
        _data = data;
        return environmentChanged;
    }

    private static bool SameMedium(SkyAtmosphere a, SkyAtmosphere b) =>
        a.GroundRadiusKilometers == b.GroundRadiusKilometers && a.AtmosphereHeightKilometers == b.AtmosphereHeightKilometers
        && a.RayleighScatteringPerKilometer.Equals(b.RayleighScatteringPerKilometer) && a.RayleighScaleHeightKilometers == b.RayleighScaleHeightKilometers
        && a.MieScatteringPerKilometer.Equals(b.MieScatteringPerKilometer) && a.MieAbsorptionPerKilometer.Equals(b.MieAbsorptionPerKilometer)
        && a.MieScaleHeightKilometers == b.MieScaleHeightKilometers && a.OzoneAbsorptionPerKilometer.Equals(b.OzoneAbsorptionPerKilometer)
        && a.OzoneCenterKilometers == b.OzoneCenterKilometers && a.OzoneHalfWidthKilometers == b.OzoneHalfWidthKilometers
        && a.GroundAlbedo.Equals(b.GroundAlbedo);

    public void EncodeSkybox(WgpuHandle<WGPURenderPassEncoder> pass)
    {
        Wgpu.SetRenderPipeline(pass, _pipelines.Skybox.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _commonGroups[3].GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    public void EncodePrefilter(int index, WgpuHandle<WGPURenderPassEncoder> pass)
    {
        Wgpu.SetRenderPipeline(pass, _pipelines.Prefilter.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _commonGroups[3].GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _prefilterGroups[index].GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    public void EncodeComposite(in GpuFrame frame, WgpuHandle<WGPUTextureView> source, WgpuHandle<WGPUTextureView> depth,
        WgpuHandle<WGPURenderPassEncoder> pass)
    {
        if (!_compositeGroup.IsValid || source.DangerousGetHandle() != _compositeSource.DangerousGetHandle()
            || depth.DangerousGetHandle() != _compositeDepth.DangerousGetHandle()) {
            if (_compositeGroup.IsValid) { _compositeGroup.Destroy(); }
            _compositeGroup = BindGroup(frame, _pipelines.CompositeLayout, [TextureEntry(0, source), TextureEntry(1, depth),
                TextureEntry(2, _views[3].GetWgpu<WGPUTextureView>()), TextureEntry(3, _views[4].GetWgpu<WGPUTextureView>())]);
            _compositeSource = source;
            _compositeDepth = depth;
        }
        Wgpu.SetRenderPipeline(pass, _pipelines.Composite.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _commonGroups[3].GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _compositeGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }
}
