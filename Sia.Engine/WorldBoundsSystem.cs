using Sia;
using Sia.Math;

namespace Sia.Engine;

public sealed class WorldBoundsSystem()
    : SystemBase(Matchers.Of<Bounds, WorldBounds, GlobalTransform>())
{
    public override void Execute(WorldContext context, IEntityQuery query)
    {
        foreach (var entity in query) {
            var local = entity.Get<Bounds>().Local;
            var worldTransform = entity.Get<GlobalTransform>().Affine;
            entity.Get<WorldBounds>().World = TransformAabb(local, worldTransform);
        }
    }

    private static Aabb TransformAabb(Aabb local, in AffineTransform worldTransform)
    {
        var min = local.Min;
        var max = local.Max;
        Span<float3> corners = [
            new(min.x, min.y, min.z), new(max.x, min.y, min.z),
            new(min.x, max.y, min.z), new(max.x, max.y, min.z),
            new(min.x, min.y, max.z), new(max.x, min.y, max.z),
            new(min.x, max.y, max.z), new(max.x, max.y, max.z),
        ];

        var first = TransformPoint(worldTransform, corners[0]);
        var result = new Aabb(first, first);
        for (var i = 1; i < corners.Length; i++) {
            result.Include(TransformPoint(worldTransform, corners[i]));
        }
        return result;
    }

    private static float3 TransformPoint(in AffineTransform worldTransform, float3 point) =>
        math.mul(worldTransform.RotationScale, point) + worldTransform.Translation;
}
