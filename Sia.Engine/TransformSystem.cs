using Sia;
using Sia.Math;
using Sia.Reactors;

namespace Sia.Engine;

public sealed class TransformSystem() : AddonSystemBase(Matchers.Of<Transform, GlobalTransform, Node<SceneGraph>>())
{
    private readonly HashSet<Entity> _visited = [];

    public override void Initialize(World world)
    {
        base.Initialize(world);
        AddAddon<Hierarchy<SceneGraph>>(world);
    }

    public override void Execute(WorldContext context, IEntityQuery query)
    {
        _visited.Clear();
        foreach (var entity in query) {
            if (entity.Get<Node<SceneGraph>>().Parent is null) {
                Propagate(entity, AffineTransform.Identity, hasParent: false);
            }
        }
    }

    private void Propagate(Entity entity, in AffineTransform parentWorld, bool hasParent)
    {
        if (!_visited.Add(entity)) {
            return;
        }

        var local = entity.Get<Transform>().ToAffine();
        var world = hasParent ? Compose(parentWorld, local) : local;
        entity.Get<GlobalTransform>().Affine = world;

        foreach (var child in entity.Get<Node<SceneGraph>>().Children) {
            if (child.Contains<Transform>() && child.Contains<GlobalTransform>()) {
                Propagate(child, world, hasParent: true);
            }
        }
    }

    private static AffineTransform Compose(
        in AffineTransform parent,
        in AffineTransform local) =>
        new(
            parent.Translation + math.mul(parent.RotationScale, local.Translation),
            math.mul(parent.RotationScale, local.RotationScale));
}
