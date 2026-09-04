using System.Reflection;
using Sia;
using Sia.Graphics.Text;
using Sia.Reactive;
using Sia.UI;
using UiText = Sia.UI.Text;
using AccentBinding = Sia.UI.StyleBinding<
    Sia.UI.Example.AccentStyle,
    Sia.UI.Example.AccentState,
    Sia.UI.Example.ShowcaseTheme,
    Sia.UI.NoStyleInteraction,
    Sia.UI.PresentationPatch>;

namespace Sia.UI.Example;

#if BROWSER
internal sealed class BrowserDomStyleShowcase : IDisposable
{
    private readonly World _world = new();
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<Entity, Command> _commands = [];
    private SystemStage? _layoutStage;
    private Font? _font;
    private Entity _theme;
    private Entity _themeButton;
    private Entity _accentButton;
    private Entity _disabledButton;
    private Entity _detailsButton;
    private Entity _sample;
    private Entity _details;
    private Entity _status;
    private State _state = State.Initial;
    private bool _disposed;

    public async Task RunAsync()
    {
        Initialize();
        var width = 0;
        var height = 0;
        var dirty = true;
        while (true) {
            while (BrowserDomPresentationAdapter.ReadEvent() is { } input) {
                ProcessInput(input);
                dirty = true;
            }

            var nextWidth = BrowserDomPresentationAdapter.ViewportWidth;
            var nextHeight = BrowserDomPresentationAdapter.ViewportHeight;
            if (nextWidth != width || nextHeight != height) {
                width = nextWidth;
                height = nextHeight;
                _entities["root"].Set(new UiRoot(new Size(width, height)));
                dirty = true;
            }

            if (dirty) {
                Update(width, height);
                dirty = false;
            }
            await Task.Delay(16);
        }
    }

    private void Initialize()
    {
        _world.AcquireAddon<UiChangeTracker>();
        _world.AcquireAddon<PresentationComposerReactor>();
        _world.AcquireAddon<RetainedUiPresentationReactor>();
        _world.AcquireAddon<UiControlInteractionReactor>();
        _world.AcquireAddon<CanvasStyle.Reactor>();
        _world.AcquireAddon<PanelStyle.Reactor>();
        _world.AcquireAddon<LabelStyle.Reactor>();
        _world.AcquireAddon<ControlStyle.Reactor>();
        _world.AcquireAddon<AccentStyle.Reactor>();
        _world.AcquireAddon<DetailsStyle.Reactor>();
        _world.Dispatcher.Listen<Activate>(OnActivate);

        _theme = _world.Create(HList.From(ShowcaseTheme.Dark));
        _font = LoadFont();
        CreateNodes();
        _layoutStage = SystemChain.Empty.AddReactiveUi().CreateStage(_world);
    }

    private void CreateNodes()
    {
        var font = _font ?? throw new InvalidOperationException("The browser font is unavailable.");
        Add("root", null, 0, HList.From(
            new UiRoot(new Size(1f, 1f)),
            CanvasStyle.Bind(default, _theme)));
        AddText(
            "header", "root", 0, "Reactive Presentation", font,
            new LabelState(30f, Heading: true));
        AddText(
            "subtitle", "root", 1,
            "State + shared Theme + ECS Interaction → typed Presentation → SVG DOM adapter",
            font,
            new LabelState(15f, Muted: true, Italic: true));
        Add("controls", "root", 2, HList.From(
            PanelStyle.Bind(new PanelState(), _theme)));
        _themeButton = AddButton("theme", "controls", 0, font, Command.ToggleTheme);
        _accentButton = AddButton("accent", "controls", 1, font, Command.ToggleAccent);
        _disabledButton = AddButton("disabled", "controls", 2, font, Command.ToggleDisabled);
        _detailsButton = AddButton("details-toggle", "controls", 3, font, Command.ToggleDetails);
        _sample = AddButton("sample", "root", 3, font, Command.ActivateSample);
        _details = Add("details", "root", 4, HList.From(
            DetailsStyle.Bind(new DetailsState(true), _theme)));
        AddText(
            "details-text", "details", 0,
            "Layout: flow, size, insets, alignment · Paint: fill, border, radius, opacity\n"
                + "Text: token, size, weight, slant, wrap · DOM: ARIA, focus, cursor, visibility",
            font,
            new LabelState(14f, Muted: true));
        _status = AddText(
            "status", "root", 5, string.Empty, font,
            new LabelState(14f, Muted: true));
        ApplyState();
    }

    private Entity AddButton(
        string key,
        string parent,
        int order,
        Font font,
        Command command)
    {
        var entity = Add(key, parent, order, HList.From(
            new UiText(string.Empty),
            new TextStyle(font, 15f, Color.White),
            new Button(),
            ControlStyle.Bind(new ControlState(key), _theme)));
        _commands.Add(entity, command);
        return entity;
    }

    private Entity AddText(
        string key,
        string parent,
        int order,
        string value,
        Font font,
        LabelState state) => Add(key, parent, order, HList.From(
        new UiText(value),
        new TextStyle(font, state.Size, Color.White),
        LabelStyle.Bind(state, _theme)));

    private Entity Add<TList>(string key, string? parent, int order, TList components)
        where TList : struct, IHList
    {
        var entity = _world.Create(HList.Cons(
            new UiNodeIdentity(key, parent, order),
            components));
        _entities.Add(key, entity);
        return entity;
    }

    private void ProcessInput(string input)
    {
        var separator = input.IndexOf('\n');
        if (separator <= 0 || !_entities.TryGetValue(input[(separator + 1)..], out var target)) {
            return;
        }
        switch (input[..separator]) {
            case "enter":
                SetMarker<Hovered>(target, true);
                break;
            case "leave":
                SetMarker<Hovered>(target, false);
                SetMarker<Pressed>(target, false);
                break;
            case "down":
                SetMarker<Pressed>(target, true);
                break;
            case "up":
                SetMarker<Pressed>(target, false);
                break;
            case "focus":
                SetMarker<Focused>(target, true);
                break;
            case "blur":
                SetMarker<Focused>(target, false);
                break;
            case "activate":
                _world.Dispatcher.Send(target, new Activate());
                break;
        }
    }

    private bool OnActivate(Entity target, scoped in Activate _)
    {
        if (!_commands.TryGetValue(target, out var command)
            || target.Contains<Disabled>()) {
            return false;
        }
        _state = command switch {
            Command.ToggleTheme => _state with { IsLight = !_state.IsLight },
            Command.ToggleAccent => _state with { UseAccent = !_state.UseAccent },
            Command.ToggleDisabled => _state with { DisableSample = !_state.DisableSample },
            Command.ToggleDetails => _state with { ShowDetails = !_state.ShowDetails },
            Command.ActivateSample => _state with { Activations = _state.Activations + 1 },
            _ => _state,
        };
        ApplyState();
        return false;
    }

    private void ApplyState()
    {
        _theme.Set(_state.IsLight ? ShowcaseTheme.Light : ShowcaseTheme.Dark);
        SetButton(
            _themeButton,
            _state.IsLight ? "Theme · light" : "Theme · dark",
            new ControlState("Toggle shared theme", Checked: _state.IsLight));
        SetButton(
            _accentButton,
            _state.UseAccent ? "Layer · accent owner mounted" : "Layer · base only",
            new ControlState("Toggle accent contribution", Checked: _state.UseAccent));
        SetButton(
            _disabledButton,
            _state.DisableSample ? "Interaction · sample disabled" : "Interaction · sample enabled",
            new ControlState("Toggle disabled ECS marker", Checked: _state.DisableSample));
        SetButton(
            _detailsButton,
            _state.ShowDetails ? "Visibility · collapse details" : "Visibility · reveal details",
            new ControlState("Toggle details visibility", Expanded: _state.ShowDetails));
        SetButton(
            _sample,
            "Interactive sample · activate me",
            new ControlState("Interactive sample"));

        SetMarker<Disabled>(_sample, _state.DisableSample);
        var accent = AccentStyle.Bind(default, _theme, StyleLayer.Variant);
        if (_state.UseAccent && !_sample.Contains<AccentBinding>()) {
            _sample.Add(accent);
        }
        else if (!_state.UseAccent && _sample.Contains<AccentBinding>()) {
            _sample.Remove<AccentBinding>();
        }
        _details.Set(DetailsStyle.Bind(new DetailsState(_state.ShowDetails), _theme));
        _status.Set(new UiText(
            $"Sample activations: {_state.Activations} · DOM events enter ECS before styles react"));
    }

    private void SetButton(Entity target, string text, ControlState state)
    {
        target.Set(new UiText(text));
        target.Set(ControlStyle.Bind(state, _theme));
    }

    private void Update(int width, int height)
    {
        if (_layoutStage is null) {
            return;
        }
        _world.FlushReactive();
        _layoutStage.Tick();
        _world.FlushReactive();
        _layoutStage.Tick();
        BrowserDomPresentationAdapter.Render(_world, width, height);
    }

    private static void SetMarker<TMarker>(Entity target, bool value)
        where TMarker : struct
    {
        if (value && !target.Contains<TMarker>()) {
            target.Add(default(TMarker));
        }
        else if (!value && target.Contains<TMarker>()) {
            target.Remove<TMarker>();
        }
    }

    private static Font LoadFont()
    {
        using var stream = typeof(BrowserDomStyleShowcase).Assembly.GetManifestResourceStream(
            "Sia.UI.Example.NotoSans-UiSubset.ttf")
            ?? throw new InvalidOperationException("The embedded browser font was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new Font(memory.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _layoutStage?.Dispose();
        _world.Dispose();
        _layoutStage = null;
        _font = null;
    }

    private readonly record struct State(
        bool IsLight,
        bool UseAccent,
        bool DisableSample,
        bool ShowDetails,
        int Activations)
    {
        public static readonly State Initial = new(false, true, false, true, 0);
    }

    private enum Command : byte
    {
        ToggleTheme,
        ToggleAccent,
        ToggleDisabled,
        ToggleDetails,
        ActivateSample,
    }
}
#endif
