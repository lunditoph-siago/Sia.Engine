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
    private readonly ulong[] _shadowGeometryRevision = new ulong[k_ShadowLayers];

    private void SetShadow(int layer, float4x4 matrix)
    {
        var at = (int)ShadowMatrixBase + (layer * 4);
        _sceneData[at] = matrix.c0;
        _sceneData[at + 1] = matrix.c1;
        _sceneData[at + 2] = matrix.c2;
        _sceneData[at + 3] = matrix.c3;
        _shadowActive[layer] = true;
        var revision = _owner.Scene.GeometryRevision;
        _shadowDirty[layer] = NeedsShadowUpdate(_shadowValid[layer], _shadowMatrices[layer], matrix, _shadowGeometryRevision[layer], revision);
        _shadowGeometryRevision[layer] = revision;
        _shadowMatrices[layer] = matrix;
        if (_selection is not null) {
            _selection.Configure(layer, matrix, _owner.Settings.ShadowResolution, _owner.Settings.ShadowResolution,
                _owner.Settings.ShadowTexelError, (uint)_owner.Settings.Streaming.MaximumSelectionNodesPerView);
            return;
        }
        // Cached depth still needs view demand and residency protection every frame.
        if (!_shadowDirty[layer] && _owner.Scene.Streaming is null) return;
        var culler = new FrustumCuller(Frustum.CreateFromViewProjection(matrix));
        var resolution = _owner.Settings.ShadowResolution;
        var projection = ProjectedGeometryError.PrepareLodProjection(matrix, resolution, resolution);
        var list = _shadowDraws[layer];
        list.Clear();
        if (_owner.Scene.Streaming is { } streaming) {
            streaming.Select(matrix, _owner.Settings.ShadowResolution, _owner.Settings.ShadowResolution,
                _owner.Settings.ShadowTexelError, culler, list);
            CompactStreamDraws(list);
        }
        else {
            foreach (var draw in _owner.Scene.Opaque)
                if (_owner.Scene.IsInstanceEnabled(draw.Instance) && culler.Intersects(draw.Bounds))
                    AddRange(list, SelectRasterLod(draw, matrix, projection, resolution, resolution, _owner.Settings.ShadowTexelError));
        }
    }

    private void Shadows(WgpuReactiveRenderGraphPassContext context)
    {
        for (uint layer = 0; layer < k_ShadowLayers; layer++) {
            if (!_shadowActive[layer]) continue;
            if (_shadowGeometryRevision[layer] != _owner.Scene.GeometryRevision) {
                _shadowDirty[layer] = true;
                _shadowGeometryRevision[layer] = _owner.Scene.GeometryRevision;
            }
            _selection?.Select(context.CommandEncoder, (int)layer);
            if (!_shadowDirty[layer]) continue;
            var attachment = WGPURenderPassDepthStencilAttachment.Default;
            attachment.View = (WGPUTextureView*)_shadowViews[layer].GetWgpu<WGPUTextureView>().DangerousGetHandle();
            attachment.DepthLoadOp = WGPULoadOp.Clear;
            attachment.DepthStoreOp = WGPUStoreOp.Store;
            attachment.DepthClearValue = 1;
            var desc = WGPURenderPassDescriptor.Default;
            desc.DepthStencilAttachment = &attachment;
            var pass = Wgpu.BeginRenderPass(context.CommandEncoder, desc);
            try {
                if (_shadowActive[layer]) DrawGeometry(pass, true, layer);
                _shadowValid[layer] = true;
            }
            finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
        }
    }

    private static bool NeedsShadowUpdate(bool valid, float4x4 previous, float4x4 current, ulong previousRevision, ulong revision)
        => !valid || !previous.Equals(current) || previousRevision != revision;
}
