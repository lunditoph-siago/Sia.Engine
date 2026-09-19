using Sia.Engine.Camera;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Conservative frustum candidates in stable back-to-front order for one view.</summary>
public sealed class TransparentDrawList
{
    private readonly Aabb[] _bounds;
    private readonly int[] _order;
    private readonly float[] _distances;
    private readonly IComparer<int> _compare;
    private float4x4? _projection;
    private float3 _eye;
    public int Count { get; private set; }
    public ReadOnlySpan<int> Items => _order.AsSpan(0, Count);

    public TransparentDrawList(ReadOnlySpan<Aabb> bounds)
    {
        _bounds = bounds.ToArray(); _order = new int[bounds.Length]; _distances = new float[bounds.Length];
        _compare = Comparer<int>.Create((a, b) => {
            var distance = _distances[b].CompareTo(_distances[a]);
            return distance != 0 ? distance : a.CompareTo(b);
        });
    }

    public bool Update(in CameraMatrices camera)
    {
        if (_projection is { } previous && previous.Equals(camera.ViewProj) && _eye.Equals(camera.WorldPosition)) return false;
        Count = 0;
        var culler = new FrustumCuller(camera.Frustum);
        for (var i = 0; i < _bounds.Length; i++) {
            if (!culler.Intersects(_bounds[i])) continue;
            _order[Count++] = i;
            _distances[i] = math.lengthsq(_bounds[i].Center - camera.WorldPosition);
        }
        Array.Sort(_order, 0, Count, _compare);
        _projection = camera.ViewProj; _eye = camera.WorldPosition;
        return true;
    }
}
