using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrRenderer
{
    internal void PrepareOutput(PbrViewState state, in GpuFrame frame,
        PbrExtractedView extracted, float exposureCompensation, PbrToneMapping toneMapping)
    {
        if (!float.IsFinite(exposureCompensation) || exposureCompensation is < -16 or > 16) {
            throw new ArgumentOutOfRangeException(nameof(exposureCompensation));
        }
        if (toneMapping is not (PbrToneMapping.Reinhard or PbrToneMapping.Aces)) {
            throw new ArgumentOutOfRangeException(nameof(toneMapping));
        }
        if (!state.SkyboxUniforms.IsValid) {
            state.SkyboxUniforms = frame.ResourceWorld.CreateWgpuBuffer(frame.Device,
                new WGPUBufferDescriptor { Size = 64, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst });
            state.ToneMappingUniforms = frame.ResourceWorld.CreateWgpuBuffer(frame.Device,
                new WGPUBufferDescriptor { Size = 16, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst });
            state.SkyboxBindGroup = frame.ResourceWorld.OwnWgpu(CreateOutputBindGroup(frame,
                outputPipelines.SkyboxLayout, state.SkyboxUniforms, 64,
                state.Ibl.PrefilteredSamplingView.GetWgpu<WGPUTextureView>(),
                state.Ibl.PrefilteredSampler.GetWgpu<WGPUSampler>()));
        }
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), state.SkyboxUniforms.GetWgpu<WGPUBuffer>(),
            0, [extracted.CameraMatrices.InvViewProj]);
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), state.ToneMappingUniforms.GetWgpu<WGPUBuffer>(),
            0, [new float4(MathF.Pow(2, exposureCompensation), outputPipelines.EncodeSrgb ? 1 : 0, (float)toneMapping, 0)]);
    }

    internal void EncodeSkybox(PbrViewState state, WgpuHandle<WGPURenderPassEncoder> pass)
    {
        if (state.ActiveAtmosphere is not null) {
            state.Atmosphere!.EncodeSkybox(pass);
            return;
        }
        Wgpu.SetRenderPipeline(pass, outputPipelines.SkyboxPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, state.SkyboxBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    internal void EncodeToneMapping(PbrViewState state, in GpuFrame frame,
        WgpuHandle<WGPUTextureView> source, WgpuHandle<WGPURenderPassEncoder> pass)
    {
        if (!state.ToneMappingBindGroup.IsValid || state.ToneMappingSource.DangerousGetHandle() != source.DangerousGetHandle()) {
            if (state.ToneMappingBindGroup.IsValid) {
                state.ToneMappingBindGroup.Destroy();
            }
            state.ToneMappingBindGroup = frame.ResourceWorld.OwnWgpu(CreateOutputBindGroup(
                frame, outputPipelines.ToneMappingLayout, state.ToneMappingUniforms, 16, source));
            state.ToneMappingSource = source;
        }
        Wgpu.SetRenderPipeline(pass, outputPipelines.ToneMappingPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, state.ToneMappingBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    private static unsafe WgpuHandle<WGPUBindGroup> CreateOutputBindGroup(in GpuFrame frame,
        Entity layout, Entity uniform, ulong size, WgpuHandle<WGPUTextureView> texture,
        WgpuHandle<WGPUSampler> sampler = default)
    {
        var entries = stackalloc WGPUBindGroupEntry[3];
        entries[0] = WGPUBindGroupEntry.Default;
        entries[0].Binding = 0;
        entries[0].Buffer = (WGPUBuffer*)uniform.GetWgpu<WGPUBuffer>().DangerousGetHandle();
        entries[0].Size = size;
        entries[1] = WGPUBindGroupEntry.Default;
        entries[1].Binding = 1;
        entries[1].TextureView = (WGPUTextureView*)texture.DangerousGetHandle();
        entries[2] = WGPUBindGroupEntry.Default;
        entries[2].Binding = 2;
        entries[2].Sampler = (WGPUSampler*)sampler.DangerousGetHandle();
        var descriptor = WGPUBindGroupDescriptor.Default;
        descriptor.Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        descriptor.EntryCount = sampler.IsNull ? 2u : 3u;
        descriptor.Entries = entries;
        return Wgpu.CreateBindGroup(frame.Device.GetWgpu<WGPUDevice>(), descriptor);
    }
}
