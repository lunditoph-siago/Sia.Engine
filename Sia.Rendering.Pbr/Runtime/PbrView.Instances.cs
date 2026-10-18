using System.Runtime.InteropServices;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrView
{
    private readonly Entity _instanceIds;
    private readonly List<PbrDrawInstance> _drawInstances = [];
    private readonly Comparison<PbrGpuScene.Draw> _sortInstanceBatches;

    private int CompareInstanceBatches(PbrGpuScene.Draw a, PbrGpuScene.Draw b)
    {
        var value = a.DoubleSided.CompareTo(b.DoubleSided);
        if (value == 0) value = _owner.Materials.MaterialBatches[a.Material].CompareTo(_owner.Materials.MaterialBatches[b.Material]);
        if (value == 0) value = _owner.Materials.Transmission[a.Material].CompareTo(_owner.Materials.Transmission[b.Material]);
        if (value == 0) value = a.FirstIndex.CompareTo(b.FirstIndex);
        return value == 0 ? a.Count.CompareTo(b.Count) : value;
    }

    private void PrepareInstanceBatches()
    {
        if (_instanceIds == default) return;
        _drawInstances.Clear();
        PbrInstanceBatching.Compact(_opaque, _drawInstances, _sortInstanceBatches);
        PbrInstanceBatching.Compact(_transparent, _drawInstances, _sortInstanceBatches, preserveOrder: true);
        for (uint layer = 0; layer < k_ShadowLayers; layer++)
            if (_shadowActive[layer] && _shadowDirty[layer])
                PbrInstanceBatching.Compact(_shadowDraws[layer], _drawInstances, _sortInstanceBatches, shadowLayer: layer);
        if (_drawInstances.Count != 0)
            Wgpu.WriteBuffer<PbrDrawInstance>(_gpu.Queue, _instanceIds.GetWgpu<WGPUBuffer>(), 0, CollectionsMarshal.AsSpan(_drawInstances));
    }

    private void BindDrawInstances(WgpuHandle<WGPURenderPassEncoder> pass, uint slot)
    {
        if (_instanceIds != default) Wgpu.SetVertexBuffer(pass, slot, _instanceIds.GetWgpu<WGPUBuffer>());
    }
}
