using Sia;
using Sia.Reactors;

namespace Sia.UI;

public class StaticStyleReactor<
    TStyle,
    TState,
    TTheme,
    TInteraction,
    TPresentation> : ReactorBase
    where TStyle : struct, IStaticStyle<TState, TTheme, TInteraction, TPresentation>
    where TState : struct
    where TTheme : struct
    where TInteraction : struct
    where TPresentation : struct
{
    private static readonly Type s_StyleType = typeof(TStyle);

    private readonly Dictionary<Entity, Entity> _themeByTarget = [];
    private readonly Dictionary<Entity, HashSet<Entity>> _targetsByTheme = [];

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        Listen<WorldEvents.Add<StyleBinding<TStyle, TState, TTheme, TInteraction, TPresentation>>>(
            OnBindingChanged);
        Listen<WorldEvents.Set<StyleBinding<TStyle, TState, TTheme, TInteraction, TPresentation>>>(
            OnBindingChanged);
        Listen<WorldEvents.Remove<StyleBinding<TStyle, TState, TTheme, TInteraction, TPresentation>>>(
            OnBindingRemoved);
        Listen<WorldEvents.Add<TTheme>>(OnThemeChanged);
        Listen<WorldEvents.Set<TTheme>>(OnThemeChanged);
        Listen<WorldEvents.Remove<TTheme>>(OnThemeChanged);
        Listen<WorldEvents.Add<TInteraction>>(OnInteractionChanged);
        Listen<WorldEvents.Set<TInteraction>>(OnInteractionChanged);
        Listen<WorldEvents.Remove<TInteraction>>(OnInteractionChanged);
        Listen<WorldEvents.Remove>(OnEntityRemoved);

        var existing = new List<Entity>();
        foreach (var host in world.Query<
                     TypeUnion<StyleBinding<
                         TStyle,
                         TState,
                         TTheme,
                         TInteraction,
                         TPresentation>>>().Hosts) {
            existing.AddRange(host);
        }
        foreach (var target in existing) {
            Track(target);
            Resolve(target);
        }
    }

    public override void OnUninitialize(World world)
    {
        foreach (var target in _themeByTarget.Keys.ToArray()) {
            Release(target);
        }
        _themeByTarget.Clear();
        _targetsByTheme.Clear();
        base.OnUninitialize(world);
    }

    private bool OnBindingChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        Track(target);
        Resolve(target);
        return false;
    }

    private bool OnBindingRemoved(
        Entity target,
        scoped in WorldEvents.Remove<StyleBinding<
            TStyle,
            TState,
            TTheme,
            TInteraction,
            TPresentation>> _)
    {
        Untrack(target);
        if (target.IsValid && !target.Contains<StyleBinding<
                TStyle,
                TState,
                TTheme,
                TInteraction,
                TPresentation>>()) {
            Release(target);
        }
        return false;
    }

    private bool OnThemeChanged<TEvent>(Entity theme, scoped in TEvent _)
        where TEvent : IEvent
    {
        if (!_targetsByTheme.TryGetValue(theme, out var targets)) {
            return false;
        }

        foreach (var target in targets.ToArray()) {
            if (!theme.IsValid || !theme.Contains<TTheme>()) {
                Release(target);
            }
            else {
                Resolve(target);
            }
        }
        return false;
    }

    private bool OnInteractionChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        if (_themeByTarget.ContainsKey(target)) {
            Resolve(target);
        }
        return false;
    }

    private bool OnEntityRemoved(Entity entity, scoped in WorldEvents.Remove _)
    {
        if (_targetsByTheme.TryGetValue(entity, out var targets)) {
            foreach (var target in targets.ToArray()) {
                Release(target);
            }
            _targetsByTheme.Remove(entity);
        }
        Untrack(entity);
        return false;
    }

    private void Track(Entity target)
    {
        ref readonly var binding = ref target.Get<StyleBinding<
            TStyle,
            TState,
            TTheme,
            TInteraction,
            TPresentation>>();
        if (_themeByTarget.Remove(target, out var previousTheme)) {
            RemoveThemeTarget(previousTheme, target);
        }

        _themeByTarget.Add(target, binding.Theme);
        if (!_targetsByTheme.TryGetValue(binding.Theme, out var targets)) {
            targets = [];
            _targetsByTheme.Add(binding.Theme, targets);
        }
        targets.Add(target);
    }

    private void Untrack(Entity target)
    {
        if (_themeByTarget.Remove(target, out var theme)) {
            RemoveThemeTarget(theme, target);
        }
    }

    private void RemoveThemeTarget(Entity theme, Entity target)
    {
        if (!_targetsByTheme.TryGetValue(theme, out var targets)) {
            return;
        }
        targets.Remove(target);
        if (targets.Count == 0) {
            _targetsByTheme.Remove(theme);
        }
    }

    private static void Resolve(Entity target)
    {
        if (!target.IsValid || !target.Contains<StyleBinding<
                TStyle,
                TState,
                TTheme,
                TInteraction,
                TPresentation>>()) {
            return;
        }

        ref readonly var binding = ref target.Get<StyleBinding<
            TStyle,
            TState,
            TTheme,
            TInteraction,
            TPresentation>>();
        if (!binding.Theme.IsValid || !binding.Theme.Contains<TTheme>()) {
            Release(target);
            return;
        }

        var interaction = target.Contains<TInteraction>()
            ? target.Get<TInteraction>()
            : default;
        var presentation = TStyle.Resolve(
            binding.State,
            binding.Theme.Get<TTheme>(),
            interaction);
        Claim(target);
        var resolved = new ResolvedStyle<TPresentation>(presentation);
        if (target.Contains<ResolvedStyle<TPresentation>>()) {
            target.Set(resolved);
        }
        else {
            target.Add(resolved);
        }
    }

    private static void Claim(Entity target)
    {
        if (!target.Contains<StyleOwner>()) {
            if (target.Contains<ResolvedStyle<TPresentation>>()) {
                throw new InvalidOperationException(
                    $"Entity '{target}' already contains an unowned resolved style.");
            }
            target.Add(new StyleOwner(s_StyleType));
            return;
        }
        if (target.Get<StyleOwner>().Style != s_StyleType) {
            throw new InvalidOperationException(
                $"Entity '{target}' is already styled by "
                + $"'{target.Get<StyleOwner>().Style.FullName}'.");
        }
    }

    private static void Release(Entity target)
    {
        if (!target.IsValid
            || !target.Contains<StyleOwner>()
            || target.Get<StyleOwner>().Style != s_StyleType) {
            return;
        }
        if (target.Contains<ResolvedStyle<TPresentation>>()) {
            target.Remove<ResolvedStyle<TPresentation>>();
        }
        target.Remove<StyleOwner>();
    }
}
