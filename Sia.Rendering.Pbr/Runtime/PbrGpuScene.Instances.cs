using Sia.Math;
using Sia.Engine.Mesh;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    private readonly PbrSceneAsset? _residentSource;
    private readonly int _instanceOffset;
    private readonly Dictionary<int, int>? _sourceSlots;
    private readonly int _sourceInstanceCount;
    private readonly PbrInstanceGpu[]? _instanceData;
    private readonly bool[]? _enabled;
    private readonly float[]? _unscaledErrors;
    private readonly (bool Transparent, int Index)[]? _drawSlots;
    private readonly HashSet<int> _dirtyInstances = [];
    private bool _boundsDirty;
    internal ulong InstanceUploadBytes { get; private set; }
    internal ReadOnlySpan<PbrInstanceGpu> InstanceData => _instanceData;

    internal bool IsEnabled(uint instance) => _enabled is null || _enabled[instance];

    internal SceneTraceData? BuildDynamicTracing(ulong maximumBytes)
    {
        var source = _residentSource ?? throw new InvalidOperationException("Dynamic transport needs live bootstrap or resident instance state.");
        var live = new List<PbrSceneInstance>();
        for (var i = 0; i < source.Instances.Length; i++) {
            var instance = source.Instances.Span[i];
            var slot = i + _instanceOffset;
            if (!instance.Dynamic || !_enabled![slot] || source.Materials.Span[instance.Material].AlphaBlend) continue;
            live.Add(instance with { Transform = _instanceData![slot].Transform });
        }
        return PbrSceneTransport.BuildDynamic(source, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(live), maximumBytes);
    }

    private int RequireDynamic(int instance)
    {
        if (_residentSource is null)
            throw new NotSupportedException("Rigid updates require authored dynamic instances.");
        if ((uint)instance >= (uint)(_sourceSlots is null ? _residentSource.Instances.Length : _sourceInstanceCount))
            throw new ArgumentOutOfRangeException(nameof(instance));
        var local = _sourceSlots is null ? instance : _sourceSlots.GetValueOrDefault(instance, -1);
        if (local < 0 || !_residentSource.Instances.Span[local].Dynamic)
            throw new InvalidOperationException("A static baked instance cannot be moved or disabled; author it as Dynamic and rebake.");
        return local + _instanceOffset;
    }

    internal void SetInstanceTransform(int instance, float4x4 transform)
    {
        instance = RequireDynamic(instance);
        PbrSceneAsset.ValidateTransform(transform);
        if (_instanceData![instance].Transform.Equals(transform)) return;
        _instanceData[instance] = _instanceData[instance] with {
            Transform = transform, NormalTransform = math.transpose(math.inverse(transform))
        };
        _dirtyInstances.Add(instance);
        var slot = _drawSlots![instance];
        if (slot.Index >= 0) {
            var draws = slot.Transparent ? Transparent : Streaming is null ? Opaque : Conventional;
            var localBounds = _residentSource!.Geometry.Span[_residentSource.Instances.Span[instance - _instanceOffset].Geometry].Build.Tree.Bounds;
            draws[slot.Index] = draws[slot.Index] with {
                Bounds = BoundsTransform.Apply(localBounds, transform),
                Error = _unscaledErrors![instance] * TransformNorm(transform)
            };
        }
        _boundsDirty = true;
        Revision++;
    }

    internal void SetInstanceEnabled(int instance, bool enabled)
    {
        instance = RequireDynamic(instance);
        if (_enabled![instance] == enabled) return;
        _enabled[instance] = enabled;
        _boundsDirty = true;
        Revision++;
    }

    internal void UploadInstances()
    {
        InstanceUploadBytes = 0;
        foreach (var instance in _dirtyInstances) {
            Wgpu.WriteBuffer<PbrInstanceGpu>(_gpu.Queue, Instances.GetWgpu<WGPUBuffer>(), (ulong)instance * 144,
                _instanceData!.AsSpan(instance, 1));
            InstanceUploadBytes += 144;
        }
        _dirtyInstances.Clear();
        if (!_boundsDirty) return;
        var minimum = new float3(float.PositiveInfinity);
        var maximum = new float3(float.NegativeInfinity);
        var any = Streaming is not null;
        if (Streaming is not null) { minimum = Streaming.Source.Bounds.Min; maximum = Streaming.Source.Bounds.Max; }
        foreach (var draw in (Streaming is null ? Opaque : Conventional).Concat(Transparent)) {
            if (!IsEnabled(draw.Instance)) continue;
            minimum = math.min(minimum, draw.Bounds.Min);
            maximum = math.max(maximum, draw.Bounds.Max);
            any = true;
        }
        Bounds = any ? new(minimum, maximum) : new(float3.zero, float3.zero);
        _boundsDirty = false;
    }
}
