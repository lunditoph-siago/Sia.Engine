using Sia;
using Sia.Reactors;

namespace Sia.UI;

public sealed class PresentationComposerReactor : ReactorBase
{
    private readonly HashSet<Entity> _targets = [];

    public override void OnInitialize(World world)
    {
        base.OnInitialize(world);
        Listen<WorldEvents.Add<StyleContributions<PresentationPatch>>>(OnChanged);
        Listen<WorldEvents.Set<StyleContributions<PresentationPatch>>>(OnChanged);
        Listen<WorldEvents.Remove<StyleContributions<PresentationPatch>>>(OnRemoved);
        Listen<WorldEvents.Remove>(OnEntityRemoved);

        var existing = new List<Entity>();
        foreach (var host in world.Query<TypeUnion<StyleContributions<PresentationPatch>>>().Hosts) {
            existing.AddRange(host);
        }
        foreach (var target in existing) {
            Compose(target);
        }
    }

    public override void OnUninitialize(World world)
    {
        foreach (var target in _targets.ToArray()) {
            RemoveResolved(target);
        }
        _targets.Clear();
        base.OnUninitialize(world);
    }

    private bool OnChanged<TEvent>(Entity target, scoped in TEvent _)
        where TEvent : IEvent
    {
        Compose(target);
        return false;
    }

    private bool OnRemoved(
        Entity target,
        scoped in WorldEvents.Remove<StyleContributions<PresentationPatch>> _)
    {
        _targets.Remove(target);
        if (target.IsValid
            && !target.Contains<StyleContributions<PresentationPatch>>()) {
            RemoveResolved(target);
        }
        return false;
    }

    private bool OnEntityRemoved(Entity target, scoped in WorldEvents.Remove _)
    {
        _targets.Remove(target);
        return false;
    }

    private void Compose(Entity target)
    {
        if (!target.IsValid || !target.Contains<StyleContributions<PresentationPatch>>()) {
            return;
        }
        var contributions = target.Get<StyleContributions<PresentationPatch>>();
        var resolved = new ResolvedStyle<Presentation>(
            PresentationComposer.Compose(contributions.Items.AsSpan()));
        _targets.Add(target);
        if (target.Contains<ResolvedStyle<Presentation>>()) {
            target.Set(resolved);
        }
        else {
            target.Add(resolved);
        }
    }

    private static void RemoveResolved(Entity target)
    {
        if (target.IsValid && target.Contains<ResolvedStyle<Presentation>>()) {
            target.Remove<ResolvedStyle<Presentation>>();
        }
    }
}
