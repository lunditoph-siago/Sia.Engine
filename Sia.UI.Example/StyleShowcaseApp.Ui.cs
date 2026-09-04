using System.Reflection;
using Sia;
using Sia.GLFW;
using Sia.Graphics.Text;
using Sia.Reactive;
using Sia.UI;
using Sia.WebGPU;
using SiaReactive = Sia.Reactive.Reactive;
using UiText = Sia.UI.Text;

namespace Sia.UI.Example;

internal sealed unsafe partial class StyleShowcaseApp
{
    private World? _uiWorld;
    private Entity _theme;
    private Font? _font;
    private UiRenderer? _uiRenderer;
    private ReactiveMount<ShowcaseProps>? _mount;
    private SystemStage? _layoutStage;
    private SystemStage? _interactionStage;
    private UiInputBinding? _input;

    private void InitializeUi()
    {
        var world = new World();
        _uiWorld = world;
        var device = world.OwnWgpu(_device, IgnoreBorrowedRelease);
        var queue = world.OwnWgpu(_queue, IgnoreBorrowedRelease);
        _uiRenderer = new UiRenderer(UiPipeline.Create(
            world,
            device,
            queue,
            _surfaceFormat));
        world.AcquireAddon<UiChangeTracker>();
        world.AcquireAddon<PresentationComposerReactor>();
        world.AcquireAddon<RetainedUiPresentationReactor>();
        world.AcquireAddon<UiControlInteractionReactor>();
        world.AcquireAddon<CanvasStyle.Reactor>();
        world.AcquireAddon<PanelStyle.Reactor>();
        world.AcquireAddon<LabelStyle.Reactor>();
        world.AcquireAddon<ControlStyle.Reactor>();
        world.AcquireAddon<AccentStyle.Reactor>();
        world.AcquireAddon<DetailsStyle.Reactor>();

        _theme = world.Create(HList.From(ShowcaseTheme.Dark));
        _font = LoadFont();
        _mount = world.Mount(
            RenderShowcase,
            new ShowcaseProps(this, new Size(InitialWidth, InitialHeight)));
        _layoutStage = SystemChain.Empty
            .AddReactiveUi()
            .CreateStage(world);
        _interactionStage = SystemChain.Empty
            .Add<UiHitTestSystem>()
            .AddUiControls()
            .CreateStage(world);
        _input = new UiInputBinding(
            _windowWorld ?? throw new InvalidOperationException("Window world is unavailable."),
            _windowEntity ?? throw new InvalidOperationException("Window entity is unavailable."),
            world);
        UpdateUi();
    }

    private static ReactiveNode RenderShowcase(
        in ShowcaseProps props,
        ref Hooks hooks)
    {
        var state = hooks.UseState(ShowcaseState.Initial);
        var snapshot = state.Value;
        var capture = new ShowcaseCapture(state, props.App._theme);
        var font = props.App._font
            ?? throw new InvalidOperationException("The showcase font is unavailable.");

        var root = ReactiveUiNode.Create(
            "root",
            null,
            0,
            default,
            HList.From(
                new UiRoot(props.Viewport),
                CanvasStyle.Bind(default, props.App._theme)));
        var header = SiaReactive.Component(
            RenderTextNode,
            new TextNodeProps(
                "header", "root", 0,
                "Reactive Presentation",
                font,
                new LabelState(30f, Heading: true),
                props.App._theme));
        var subtitle = SiaReactive.Component(
            RenderTextNode,
            new TextNodeProps(
                "subtitle", "root", 1,
                "State + shared Theme + ECS Interaction → typed Presentation → native/browser GPU adapter",
                font,
                new LabelState(15f, Muted: true),
                props.App._theme));
        var controls = ReactiveUiNode.Create(
            "controls",
            "root",
            2,
            default,
            HList.From(PanelStyle.Bind(new PanelState(), props.App._theme)));
        var theme = SiaReactive.Component(
            RenderCommandNode,
            new CommandNodeProps(
                "theme", "controls", 0,
                snapshot.IsLight ? "Theme · light" : "Theme · dark",
                font,
                new ControlState("Toggle shared theme", Checked: snapshot.IsLight),
                capture,
                ShowcaseCommand.ToggleTheme));
        var accent = SiaReactive.Component(
            RenderCommandNode,
            new CommandNodeProps(
                "accent", "controls", 1,
                snapshot.UseAccent ? "Layer · accent owner mounted" : "Layer · base only",
                font,
                new ControlState("Toggle accent contribution", Checked: snapshot.UseAccent),
                capture,
                ShowcaseCommand.ToggleAccent));
        var disabled = SiaReactive.Component(
            RenderCommandNode,
            new CommandNodeProps(
                "disabled", "controls", 2,
                snapshot.DisableSample ? "Interaction · sample disabled" : "Interaction · sample enabled",
                font,
                new ControlState("Toggle disabled ECS marker", Checked: snapshot.DisableSample),
                capture,
                ShowcaseCommand.ToggleDisabled));
        var detailsToggle = SiaReactive.Component(
            RenderCommandNode,
            new CommandNodeProps(
                "details-toggle", "controls", 3,
                snapshot.ShowDetails ? "Visibility · collapse details" : "Visibility · reveal details",
                font,
                new ControlState("Toggle details visibility", Expanded: snapshot.ShowDetails),
                capture,
                ShowcaseCommand.ToggleDetails));
        var sample = SiaReactive.Component(
            RenderSample,
            new SampleProps(
                font,
                props.App._theme,
                snapshot.UseAccent,
                snapshot.DisableSample,
                capture));
        var details = ReactiveUiNode.Create(
            "details",
            "root",
            4,
            default,
            HList.From(DetailsStyle.Bind(
                new DetailsState(snapshot.ShowDetails),
                props.App._theme)));
        var detailsText = SiaReactive.Component(
            RenderTextNode,
            new TextNodeProps(
                "details-text", "details", 0,
                "Layout: flow, size, insets, alignment  ·  Paint: fill, border, radius, opacity\n"
                    + "Text: size, weight, wrap  ·  Semantics: role, name, checked, expanded, disabled",
                font,
                new LabelState(14f, Muted: true),
                props.App._theme));
        var status = SiaReactive.Component(
            RenderTextNode,
            new TextNodeProps(
                "status", "root", 5,
                $"Sample activations: {snapshot.Activations}  ·  hover, press and keyboard focus are projected from ECS markers",
                font,
                new LabelState(14f, Muted: true),
                props.App._theme));

        return SiaReactive.Group(
            SiaReactive.Group(
                root,
                header,
                subtitle,
                controls,
                theme,
                accent),
            SiaReactive.Group(
                disabled,
                detailsToggle,
                sample,
                details,
                detailsText,
                status));
    }

    private static ReactiveNode RenderSample(in SampleProps props, ref Hooks hooks)
    {
        _ = hooks;
        var reactions = SiaReactive.Group(
            SiaReactive.On<Activate, ShowcaseCapture>(
                props.Capture,
                static (in Activate _, in ShowcaseCapture capture) =>
                    Apply(capture, ShowcaseCommand.ActivateSample)));
        var text = new UiText("Interactive sample · activate me");
        var textStyle = new TextStyle(props.Font, 15f, Color.White);
        var baseStyle = ControlStyle.Bind(
            new ControlState("Interactive sample"),
            props.Theme);

        if (props.UseAccent && props.IsDisabled) {
            return ReactiveUiNode.Create(
                "sample", "root", 3, default,
                HList.From(
                    text,
                    textStyle,
                    new Button(),
                    new Disabled(),
                    baseStyle,
                    AccentStyle.Bind(default, props.Theme, StyleLayer.Variant)),
                reactions);
        }
        if (props.UseAccent) {
            return ReactiveUiNode.Create(
                "sample", "root", 3, default,
                HList.From(
                    text,
                    textStyle,
                    new Button(),
                    baseStyle,
                    AccentStyle.Bind(default, props.Theme, StyleLayer.Variant)),
                reactions);
        }
        if (props.IsDisabled) {
            return ReactiveUiNode.Create(
                "sample", "root", 3, default,
                HList.From(text, textStyle, new Button(), new Disabled(), baseStyle),
                reactions);
        }
        return ReactiveUiNode.Create(
            "sample", "root", 3, default,
            HList.From(text, textStyle, new Button(), baseStyle),
            reactions);
    }

    private static ReactiveNode RenderCommandNode(
        in CommandNodeProps props,
        ref Hooks hooks)
    {
        _ = hooks;
        return ReactiveUiNode.Create(
            props.Key,
            props.Parent,
            props.Order,
            default,
            HList.From(
                new UiText(props.Label),
                new TextStyle(props.Font, 15f, Color.White),
                new Button(),
                ControlStyle.Bind(props.Style, props.Capture.Theme)),
            SiaReactive.Group(
                SiaReactive.On<Activate, CommandCapture>(
                    new(props.Capture, props.Command),
                    static (in Activate _, in CommandCapture commandCapture) =>
                        Apply(commandCapture.Showcase, commandCapture.Command))));
    }

    private static ReactiveNode RenderTextNode(
        in TextNodeProps props,
        ref Hooks hooks)
    {
        _ = hooks;
        return ReactiveUiNode.Create(
            props.Key,
            props.Parent,
            props.Order,
            default,
            HList.From(
                new UiText(props.Value),
                new TextStyle(props.Font, props.Style.Size, Color.White),
                LabelStyle.Bind(props.Style, props.Theme)));
    }

    private static void Apply(
        scoped in ShowcaseCapture capture,
        ShowcaseCommand command)
    {
        var current = capture.State.Value;
        var next = command switch {
            ShowcaseCommand.ToggleTheme => current with { IsLight = !current.IsLight },
            ShowcaseCommand.ToggleAccent => current with { UseAccent = !current.UseAccent },
            ShowcaseCommand.ToggleDisabled => current with {
                DisableSample = !current.DisableSample,
            },
            ShowcaseCommand.ToggleDetails => current with { ShowDetails = !current.ShowDetails },
            ShowcaseCommand.ActivateSample => current with {
                Activations = current.Activations + 1,
            },
            _ => current,
        };
        if (next.IsLight != current.IsLight) {
            capture.Theme.Set(next.IsLight ? ShowcaseTheme.Light : ShowcaseTheme.Dark);
        }
        capture.State.Set(next);
    }

    private void ResizeUi(Size viewport)
    {
        if (_mount is { } mount) {
            mount.Update(new ShowcaseProps(this, viewport));
        }
        if (_input is not null && _windowWorld is not null) {
            var windowSize = Glfw.GetSize(_window);
            _input.SetPointerSpace(
                new Size(windowSize.Width, windowSize.Height),
                viewport);
        }
    }

    private void UpdateUi()
    {
        if (_uiWorld is null || _layoutStage is null || _interactionStage is null) {
            return;
        }
        _uiWorld.FlushReactive();
        _layoutStage.Tick();
        _interactionStage.Tick();
        _uiWorld.FlushReactive();
        _layoutStage.Tick();
    }

    private void RenderUi(
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        Size viewport)
    {
        if (_uiWorld is null || _uiRenderer is null) {
            return;
        }
        var primitiveCount = _uiRenderer.PrepareFrame(_uiWorld, viewport);
        _uiRenderer.Encode(renderPass, primitiveCount);
    }

    private static Font LoadFont()
    {
        var assembly = typeof(StyleShowcaseApp).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "Sia.UI.Example.NotoSans-UiSubset.ttf")
            ?? throw new InvalidOperationException("The embedded showcase font was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new Font(memory.ToArray());
    }

    private void DisposeUi()
    {
        _input?.Dispose();
        _input = null;
        if (_mount is { IsMounted: true } mount) {
            mount.Unmount();
        }
        _interactionStage?.Dispose();
        _layoutStage?.Dispose();
        _uiWorld?.Dispose();
        _mount = null;
        _interactionStage = null;
        _layoutStage = null;
        _uiRenderer = null;
        _font = null;
        _uiWorld = null;
        _theme = default;
    }

    private static void IgnoreBorrowedRelease<T>(ref WgpuHandle<T> handle)
        where T : unmanaged
    {
        _ = handle;
    }

    private readonly record struct ShowcaseProps(
        StyleShowcaseApp App,
        Size Viewport);

    private readonly record struct ShowcaseState(
        bool IsLight,
        bool UseAccent,
        bool DisableSample,
        bool ShowDetails,
        int Activations)
    {
        public static readonly ShowcaseState Initial = new(
            IsLight: false,
            UseAccent: true,
            DisableSample: false,
            ShowDetails: true,
            Activations: 0);
    }

    private readonly record struct ShowcaseCapture(
        State<ShowcaseState> State,
        Entity Theme);

    private readonly record struct CommandCapture(
        ShowcaseCapture Showcase,
        ShowcaseCommand Command);

    private readonly record struct SampleProps(
        Font Font,
        Entity Theme,
        bool UseAccent,
        bool IsDisabled,
        ShowcaseCapture Capture);

    private readonly record struct CommandNodeProps(
        string Key,
        string Parent,
        int Order,
        string Label,
        Font Font,
        ControlState Style,
        ShowcaseCapture Capture,
        ShowcaseCommand Command);

    private readonly record struct TextNodeProps(
        string Key,
        string Parent,
        int Order,
        string Value,
        Font Font,
        LabelState Style,
        Entity Theme);

    private enum ShowcaseCommand : byte
    {
        ToggleTheme,
        ToggleAccent,
        ToggleDisabled,
        ToggleDetails,
        ActivateSample,
    }
}
