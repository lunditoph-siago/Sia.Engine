using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;
using CameraComponent = Sia.Engine.Camera.Camera;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void Timestamp(WgpuReactiveRenderGraphPassContext context, uint point)
    {
        if (!_owner.SampleGpuTiming) return;
        var writes = new WGPUPassTimestampWrites {
            QuerySet = (WGPUQuerySet*)_queries.GetWgpu<WGPUQuerySet>().DangerousGetHandle(),
            BeginningOfPassWriteIndex = point * 2,
            EndOfPassWriteIndex = (point * 2) + 1
        };
        var descriptor = WGPUComputePassDescriptor.Default;
        descriptor.TimestampWrites = &writes;
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, descriptor);
        Wgpu.EndComputePass(pass);
        Wgpu.Release(ref pass);
        var last = (uint)PbrRenderer.GpuTimingStages.Length - 1;
        if (point == last) {
            Wgpu.ResolveQuerySet(context.CommandEncoder, _queries.GetWgpu<WGPUQuerySet>(), 0, (last + 1) * 2, _queryScratch.GetWgpu<WGPUBuffer>(), 0);
            Wgpu.CopyBufferToBuffer(context.CommandEncoder, _queryScratch.GetWgpu<WGPUBuffer>(), 0, _timing.GetWgpu<WGPUBuffer>(), 0, 8);
            Wgpu.CopyBufferToBuffer(context.CommandEncoder, _queryScratch.GetWgpu<WGPUBuffer>(), ((last * 2) + 1) * 8, _timing.GetWgpu<WGPUBuffer>(), 8, 8);
            for (ulong stage = 0; stage < last; stage++) {
                Wgpu.CopyBufferToBuffer(context.CommandEncoder, _queryScratch.GetWgpu<WGPUBuffer>(), ((stage * 2) + 1) * 8, _timing.GetWgpu<WGPUBuffer>(), (stage + 1) * 16, 8);
                Wgpu.CopyBufferToBuffer(context.CommandEncoder, _queryScratch.GetWgpu<WGPUBuffer>(), ((stage * 2) + 2) * 8, _timing.GetWgpu<WGPUBuffer>(), ((stage + 1) * 16) + 8, 8);
            }
        }
    }
}
