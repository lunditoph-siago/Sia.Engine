using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Per-view committed history validity. Preparation/encoding cannot claim a submitted frame.</summary>
public sealed class PbrReflectionHistoryState
{
    private ulong _committedFrame;
    private uint _width, _height;
    private float4x4 _viewProjection;
    private float3 _eye;
    private bool _valid;
    private ulong _pendingFrame;
    private bool _pending;
    private bool _encoded;
    private uint _pendingWidth, _pendingHeight;
    private float4x4 _pendingViewProjection;
    private float3 _pendingEye;
    public float4x4 PreviousViewProjection => _viewProjection;
    public float3 PreviousEye => _eye;

    public bool Prepare(ulong frame, uint width, uint height, float4x4 viewProjection, float3 eye,
        float cutDistance, bool cameraCut = false)
    {
        if (width == 0 || height == 0 || !math.all(math.isfinite(eye)) || !float.IsFinite(cutDistance) || cutDistance <= 0)
            throw new ArgumentException("Invalid history frame inputs.");
        if (!math.all(math.isfinite(viewProjection.c0)) || !math.all(math.isfinite(viewProjection.c1))
            || !math.all(math.isfinite(viewProjection.c2)) || !math.all(math.isfinite(viewProjection.c3)))
            throw new ArgumentException("History camera must be finite.");
        var reusable = _valid && !cameraCut && _committedFrame != ulong.MaxValue && frame == _committedFrame + 1
            && width == _width && height == _height && math.distance(eye, _eye) <= cutDistance;
        if (!reusable) _valid = false;
        _pendingFrame = frame; _pendingWidth = width; _pendingHeight = height; _pendingViewProjection = viewProjection; _pendingEye = eye;
        _pending = true; _encoded = false; return reusable;
    }

    public void Encoded(ulong frame)
    {
        if (!_pending || frame != _pendingFrame || _encoded) throw new InvalidOperationException("Encode history once for the prepared frame.");
        _encoded = true;
    }

    public void Submitted(ulong frame)
    {
        if (!_pending || !_encoded || frame != _pendingFrame) throw new InvalidOperationException("History requires matching encoded and submitted work.");
        _committedFrame = frame; _width = _pendingWidth; _height = _pendingHeight; _viewProjection = _pendingViewProjection; _eye = _pendingEye;
        _valid = true; _pending = false; _encoded = false;
    }

    public void Invalidate() { _valid = false; _pending = false; _encoded = false; }
}
