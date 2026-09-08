using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrRenderer
{
    private Entity _visibilityPipeline;
    private Entity _visibilityLayout;

    internal unsafe void PrepareVisibility(PbrViewState state, in GpuFrame frame, PbrExtractedView extracted, VisibilityDebugMode mode)
    {
        if (!_visibilityPipeline.IsValid) { CreateVisibilityPipeline(in frame); }
        if (!state.VisibilityUniforms.IsValid) {
            state.VisibilityUniforms = frame.ResourceWorld.CreateWgpuBuffer(frame.Device,
                new WGPUBufferDescriptor { Size = 96, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst });
        }
        var camera = extracted.CameraMatrices;
        Wgpu.WriteBuffer<VisibilityLightingCamera>(frame.Queue.GetWgpu<WGPUQueue>(), state.VisibilityUniforms.GetWgpu<WGPUBuffer>(), 0,
            [new(camera.InvViewProj, new(camera.WorldPosition, 1), new((uint)mode, 0, 0, 0))]);
    }

    private unsafe void CreateVisibilityPipeline(in GpuFrame frame)
    {
        var acquired = new List<Entity>();
        var world = frame.ResourceWorld;
        var device = frame.Device.GetWgpu<WGPUDevice>();
        try {
            var entries = stackalloc WGPUBindGroupLayoutEntry[6];
            entries[0] = WGPUBindGroupLayoutEntry.Default;
            entries[0].Binding = 0;
            entries[0].Visibility = WGPUShaderStage.Fragment;
            entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;
            entries[0].Buffer.MinBindingSize = 96;
            for (uint i = 1; i < 6; i++) {
                entries[i] = WGPUBindGroupLayoutEntry.Default;
                entries[i].Binding = i;
                entries[i].Visibility = WGPUShaderStage.Fragment;
                entries[i].Texture.SampleType = i == 1 ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float;
                entries[i].Texture.ViewDimension = WGPUTextureViewDimension._2D;
            }
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = 6; descriptor.Entries = entries;
            var layout = Own(Wgpu.CreateBindGroupLayout(device, descriptor));
            var pipelineLayout = Own(PbrObjectBindGroupLayout.CreatePipelineLayout(device, layout.GetWgpu<WGPUBindGroupLayout>(),
                forwardPipeline.LightingBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(), forwardPipeline.IblBindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
            var shader = Own(Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityLighting(), "visibility-scene-lighting"));
            var pipeline = Own(PbrIblPrecomputePipelines.CreateFullscreenPipeline(device, shader.GetWgpu<WGPUShaderModule>(),
                pipelineLayout.GetWgpu<WGPUPipelineLayout>(), PbrOutputPipelines.HdrFormat));
            _visibilityLayout = layout; _visibilityPipeline = pipeline;
        }
        catch { for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); } throw; }

        Entity Own<T>(WgpuHandle<T> handle) where T : unmanaged
        {
            var entity = world.OwnWgpu(handle); acquired.Add(entity); return entity;
        }
    }

    internal unsafe void EncodeVisibility(PbrViewState state, in GpuFrame frame, ReadOnlySpan<WgpuHandle<WGPUTextureView>> surfaces,
        WgpuHandle<WGPURenderPassEncoder> pass)
    {
        if (!state.VisibilityBindGroup.IsValid || !state.VisibilitySources.AsSpan().SequenceEqual(surfaces)) {
            var retained = surfaces.ToArray();
            var entries = new WGPUBindGroupEntry[6];
            entries[0] = WGPUBindGroupEntry.Default;
            entries[0].Buffer = (WGPUBuffer*)state.VisibilityUniforms.GetWgpu<WGPUBuffer>().DangerousGetHandle();
            entries[0].Size = 96;
            for (uint i = 0; i < 5; i++) {
                entries[i + 1] = WGPUBindGroupEntry.Default;
                entries[i + 1].Binding = i + 1;
                entries[i + 1].TextureView = (WGPUTextureView*)retained[i].DangerousGetHandle();
            }
            var next = PbrTextureBindGroups.Create(frame.ResourceWorld, frame.Device.GetWgpu<WGPUDevice>(), _visibilityLayout, entries, retained);
            if (state.VisibilityBindGroup.IsValid) { state.VisibilityBindGroup.Destroy(); }
            state.VisibilityBindGroup = next;
            state.VisibilitySources = retained;
        }
        Wgpu.SetRenderPipeline(pass, _visibilityPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, state.VisibilityBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, state.ForwardLightingBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 2, state.IblBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct VisibilityLightingCamera(float4x4 InverseViewProjection, float4 Eye, uint4 Mode);
}
