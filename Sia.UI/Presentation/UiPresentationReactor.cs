using Sia;
using Sia.Reactors;

namespace Sia.UI;

public sealed class UiPresentationReactor : ReactorBase
{
    private readonly Dictionary<Entity, Baseline> _baselines = [];

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        Listen<WorldEvents.Add<ResolvedStyle<Presentation>>>(OnPresentationChanged);
        Listen<WorldEvents.Set<ResolvedStyle<Presentation>>>(OnPresentationChanged);
        Listen<WorldEvents.Remove<ResolvedStyle<Presentation>>>(OnPresentationRemoved);
        Listen<WorldEvents.Remove>(OnEntityRemoved);

        foreach (var host in world.Query<TypeUnion<ResolvedStyle<Presentation>>>().Hosts) {
            foreach (var target in host) {
                Apply(target);
            }
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

    private bool OnPresentationChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        Apply(target);
        return false;
    }

    private bool OnPresentationRemoved(
        Entity target,
        scoped in WorldEvents.Remove<ResolvedStyle<Presentation>> _)
    {
        Restore(target);
        return false;
    }

    private bool OnEntityRemoved(Entity target, scoped in WorldEvents.Remove _)
    {
        _baselines.Remove(target);
        return false;
    }

    private void Apply(Entity target)
    {
        if (!target.IsValid || !target.Contains<ResolvedStyle<Presentation>>()) {
            return;
        }
        if (!_baselines.ContainsKey(target)) {
            _baselines.Add(target, Baseline.Capture(target));
        }

        var presentation = target
            .Get<ResolvedStyle<Presentation>>()
            .Presentation;
        Set(target, UiPresentationMapper.Node(presentation));
        Set(target, new BackgroundColor(UiPresentationMapper.Color(
            presentation.Paint.Background,
            presentation.Paint.Opacity)));
        Set(target, new BorderColor(UiPresentationMapper.Color(
            presentation.Paint.Border,
            presentation.Paint.Opacity)));
        Set(target, new ZIndex(presentation.Layout.Layer));

        if (target.Contains<TextStyle>()) {
            var text = target.Get<TextStyle>();
            var size = presentation.Typography.Size.Kind == LayoutLengthKind.Logical
                ? presentation.Typography.Size.Value
                : text.FontSize;
            target.Set(text with {
                FontSize = size,
                Color = UiPresentationMapper.Color(
                    presentation.Paint.Foreground,
                    presentation.Paint.Opacity),
            });
        }
    }

    private void Restore(Entity target)
    {
        if (!_baselines.Remove(target, out var baseline) || !target.IsValid) {
            return;
        }
        Restore(target, baseline.Node);
        Restore(target, baseline.Background);
        Restore(target, baseline.Border);
        Restore(target, baseline.ZIndex);
        Restore(target, baseline.Text);
    }

    private static void Set<TComponent>(Entity target, in TComponent component)
    {
        if (target.Contains<TComponent>()) {
            target.Set(component);
        }
        else {
            target.Add(component);
        }
    }

    private static void Restore<TComponent>(Entity target, in Component<TComponent> component)
    {
        if (component.Exists) {
            Set(target, component.Value);
        }
        else if (target.Contains<TComponent>()) {
            target.Remove<TComponent>();
        }
    }

    private readonly record struct Baseline(
        Component<Node> Node,
        Component<BackgroundColor> Background,
        Component<BorderColor> Border,
        Component<ZIndex> ZIndex,
        Component<TextStyle> Text)
    {
        public static Baseline Capture(Entity target) => new(
            Component<Node>.Capture(target),
            Component<BackgroundColor>.Capture(target),
            Component<BorderColor>.Capture(target),
            Component<ZIndex>.Capture(target),
            Component<TextStyle>.Capture(target));
    }

    private readonly record struct Component<TComponent>(bool Exists, TComponent Value)
    {
        public static Component<TComponent> Capture(Entity target)
            => target.Contains<TComponent>()
                ? new(true, target.Get<TComponent>())
                : default;
    }
}
