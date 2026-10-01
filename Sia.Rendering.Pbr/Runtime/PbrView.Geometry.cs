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
    private static PbrGpuScene.Draw SelectGeometry(PbrGpuScene.Draw draw, float4x4 vp, uint width, uint height, float target)
    {
        if (target <= 0 || draw.CoarseCount == 0) return draw;
        var error = ProjectedGeometryError.ProjectError(draw.Bounds, draw.Error, vp, width, height);
        return error <= target ? draw with {
            First = draw.CoarseFirst,
            Count = draw.CoarseCount,
            FirstIndex = draw.CoarseFirst * 3
        } : draw;
    }

    private void AddRange(List<PbrGpuScene.Draw> list, PbrGpuScene.Draw draw, bool matchBatch = false)
    {
        if (list.Count != 0 && list[^1].DoubleSided == draw.DoubleSided && list[^1].First + list[^1].Count == draw.First
            && (!matchBatch || _owner.Materials.MaterialBatches[list[^1].Material] == _owner.Materials.MaterialBatches[draw.Material]))
            list[^1] = list[^1] with {
                Count = list[^1].Count + draw.Count
            };
        else
            list.Add(draw);
    }

    private void CompactStreamDraws(List<PbrGpuScene.Draw> list, bool matchBatch = false)
    {
        list.Sort((a, b) => {
            var side = a.DoubleSided.CompareTo(b.DoubleSided);
            if (side != 0) return side;
            if (matchBatch) {
                var batch = _owner.Materials.MaterialBatches[a.Material].CompareTo(_owner.Materials.MaterialBatches[b.Material]);
                if (batch != 0) return batch;
            }
            return a.First.CompareTo(b.First);
        });
        var write = 0;
        for (var read = 0; read < list.Count; read++) {
            var draw = list[read];
            if (write > 0 && list[write - 1].DoubleSided == draw.DoubleSided
                && list[write - 1].First + list[write - 1].Count == draw.First
                && (!matchBatch || _owner.Materials.MaterialBatches[list[write - 1].Material] == _owner.Materials.MaterialBatches[draw.Material]))
                list[write - 1] = list[write - 1] with {
                    Count = list[write - 1].Count + draw.Count,
                    Bounds = new(math.min(list[write - 1].Bounds.Min, draw.Bounds.Min), math.max(list[write - 1].Bounds.Max, draw.Bounds.Max))
                };
            else list[write++] = draw;
        }
        if (write < list.Count) list.RemoveRange(write, list.Count - write);
    }

    private void DrawGeometry(WgpuHandle<WGPURenderPassEncoder> pass, bool shadow, uint layer = 0)
    {
        if (_selection is not null) {
            DrawStreamGeometry(pass, shadow, layer);
            return;
        }
        Wgpu.SetBindGroup(pass, 0, _rasterGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        if (shadow) Wgpu.SetVertexBuffer(pass, 0, _owner.Scene.Vertices.GetWgpu<WGPUBuffer>());
        if (shadow) Wgpu.SetIndexBuffer(pass, _owner.Scene.Topology.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
        bool? doubleSided = null;
        foreach (var draw in shadow ? _shadowDraws[layer] : _opaque) {
            if (doubleSided != draw.DoubleSided) {
                var pipeline = shadow ? (draw.DoubleSided ? _owner.Pipelines.ShadowDouble : _owner.Pipelines.Shadow)
                    : (draw.DoubleSided ? _owner.Pipelines.RasterDouble : _owner.Pipelines.Raster);
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>());
                doubleSided = draw.DoubleSided;
            }
            if (shadow)
                Wgpu.DrawIndexed(pass, draw.Count * 3, 1, draw.FirstIndex, firstInstance: (draw.Instance << 3) | layer);
            else Wgpu.Draw(pass, draw.Count * 3, 1, draw.First * 3);
        }
    }

    private void Raster(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_idKey, WGPULoadOp.Clear),
            new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Clear));
        DrawGeometry(pass, false);
    }

    private void BindShading(WgpuHandle<WGPUComputePassEncoder> pass, Entity io)
    {
        Wgpu.SetBindGroup(pass, 0, _frameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[0].GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 3, io.GetWgpu<WGPUBindGroup>());
    }

    private void Tiles(WgpuReactiveRenderGraphPassContext context)
    {
        if (_owner.Materials.Groups.Length == 1) return;
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            BindShading(pass, _tileGroup);
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.TileReset.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, ((uint)_owner.Materials.Groups.Length + 63) / 64);
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.TileClassify.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, (_width + 7) / 8, (_height + 7) / 8);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    private void Shade(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            BindShading(pass, _resolveGroup);
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.Background.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, (_width + 7) / 8, (_height + 7) / 8);
            if (_owner.Materials.Groups.Length == 1) {
                Wgpu.SetComputePipeline(pass, _owner.Pipelines.ResolveDirect.GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, (_width + 7) / 8, (_height + 7) / 8);
                return;
            }
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.Resolve.GetWgpu<WGPUComputePipeline>());
            for (var batch = 0; batch < _owner.Materials.Groups.Length; batch++) {
                Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[batch].GetWgpu<WGPUBindGroup>());
                Wgpu.DispatchWorkgroupsIndirect(pass, _tiles.GetWgpu<WGPUBuffer>(), (ulong)batch * _tileStride * 4);
            }
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }
}
