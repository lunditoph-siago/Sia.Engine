using Sia;
using Sia.Reactors;

namespace Sia.UI;

public sealed class UiControlInteractionReactor : ReactorBase
{
    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        Listen<WorldEvents.Add<Hovered>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Hovered>>(OnMarkerChanged);
        Listen<WorldEvents.Add<Pressed>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Pressed>>(OnMarkerChanged);
        Listen<WorldEvents.Add<Focused>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Focused>>(OnMarkerChanged);
        Listen<WorldEvents.Add<Disabled>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Disabled>>(OnMarkerChanged);

        foreach (var host in world.Query<TypeUnion<Hovered, Pressed, Focused, Disabled>>().Hosts) {
            foreach (var target in host) {
                Synchronize(target);
            }
        }
    }

    private static bool OnMarkerChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        Synchronize(target);
        return false;
    }

    private static void Synchronize(Entity target)
    {
        if (!target.IsValid) {
            return;
        }
        var interaction = new ControlInteraction {
            IsHovered = target.Contains<Hovered>(),
            IsPressed = target.Contains<Pressed>(),
            IsFocused = target.Contains<Focused>(),
            IsDisabled = target.Contains<Disabled>(),
        };
        if (interaction == default) {
            if (target.Contains<ControlInteraction>()) {
                target.Remove<ControlInteraction>();
            }
        }
        else if (target.Contains<ControlInteraction>()) {
            target.Set(interaction);
        }
        else {
            target.Add(interaction);
        }
    }
}
