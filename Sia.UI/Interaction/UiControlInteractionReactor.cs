using Sia;
using Sia.Reactors;

namespace Sia.UI;

public sealed class UiControlInteractionReactor : ReactorBase
{
    private readonly Dictionary<Entity, Component<ControlInteraction>> _baselines = [];

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        Listen<WorldEvents.Add<Hovered>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Hovered>>(OnHoveredRemoved);
        Listen<WorldEvents.Add<Pressed>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Pressed>>(OnPressedRemoved);
        Listen<WorldEvents.Add<Focused>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Focused>>(OnFocusedRemoved);
        Listen<WorldEvents.Add<Disabled>>(OnMarkerChanged);
        Listen<WorldEvents.Remove<Disabled>>(OnDisabledRemoved);
        Listen<WorldEvents.Remove>(OnEntityRemoved);

        var existing = new List<Entity>();
        foreach (var host in world.Query<TypeUnion<Hovered, Pressed, Focused, Disabled>>().Hosts) {
            existing.AddRange(host);
        }
        foreach (var target in existing) {
            Synchronize(target);
        }
    }

    public override void OnUninitialize(World world)
    {
        foreach (var target in _baselines.Keys.ToArray()) {
            Restore(target);
        }
        _baselines.Clear();
        base.OnUninitialize(world);
    }

    private bool OnMarkerChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        Synchronize(target);
        return false;
    }

    private bool OnHoveredRemoved(
        Entity target,
        scoped in WorldEvents.Remove<Hovered> _)
        => OnMarkerRemoved<Hovered>(target);

    private bool OnPressedRemoved(
        Entity target,
        scoped in WorldEvents.Remove<Pressed> _)
        => OnMarkerRemoved<Pressed>(target);

    private bool OnFocusedRemoved(
        Entity target,
        scoped in WorldEvents.Remove<Focused> _)
        => OnMarkerRemoved<Focused>(target);

    private bool OnDisabledRemoved(
        Entity target,
        scoped in WorldEvents.Remove<Disabled> _)
        => OnMarkerRemoved<Disabled>(target);

    private bool OnMarkerRemoved<TMarker>(Entity target)
        where TMarker : struct
    {
        if (target.IsValid && !target.Contains<TMarker>()) {
            Synchronize(target);
        }
        return false;
    }

    private bool OnEntityRemoved(Entity target, scoped in WorldEvents.Remove _)
    {
        _baselines.Remove(target);
        return false;
    }

    private void Synchronize(Entity target)
    {
        if (!target.IsValid) {
            _baselines.Remove(target);
            return;
        }
        var interaction = new ControlInteraction {
            IsHovered = target.Contains<Hovered>(),
            IsPressed = target.Contains<Pressed>(),
            IsFocused = target.Contains<Focused>(),
            IsDisabled = target.Contains<Disabled>(),
        };
        if (interaction == default) {
            Restore(target);
        }
        else {
            if (!_baselines.ContainsKey(target)) {
                _baselines.Add(target, Component<ControlInteraction>.Capture(target));
            }
            Set(target, interaction);
        }
    }

    private void Restore(Entity target)
    {
        if (!_baselines.Remove(target, out var baseline) || !target.IsValid) {
            return;
        }
        if (baseline.Exists) {
            Set(target, baseline.Value);
        }
        else if (target.Contains<ControlInteraction>()) {
            target.Remove<ControlInteraction>();
        }
    }

    private static void Set(Entity target, scoped in ControlInteraction interaction)
    {
        if (target.Contains<ControlInteraction>()) {
            target.Set(interaction);
        }
        else {
            target.Add(interaction);
        }
    }

    private readonly record struct Component<TComponent>(bool Exists, TComponent Value)
    {
        public static Component<TComponent> Capture(Entity target)
            => target.Contains<TComponent>()
                ? new(true, target.Get<TComponent>())
                : default;
    }
}
