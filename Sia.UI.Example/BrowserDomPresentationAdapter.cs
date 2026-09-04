using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia;
using Sia.UI;

namespace Sia.UI.Example;

#if BROWSER
internal static partial class BrowserDomPresentationAdapter
{
    public static void Render(World world, int width, int height)
    {
        var nodes = new List<BrowserDomNode>();
        var query = world.Query(Matchers.Of<
            UiNodeIdentity,
            ComputedNode,
            UiGlobalTransform,
            ResolvedStyle<Presentation>>());
        foreach (var entity in query) {
            var identity = entity.Get<UiNodeIdentity>();
            var computed = entity.Get<ComputedNode>();
            var transform = entity.Get<UiGlobalTransform>();
            var presentation = entity.Get<ResolvedStyle<Presentation>>().Presentation;
            var text = entity.Contains<Text>() ? entity.Get<Text>().Value : string.Empty;
            var visible = UiVisibility.IsVisible(entity);
            nodes.Add(new BrowserDomNode(
                identity.Key,
                text,
                transform.Tx,
                transform.Ty,
                computed.Size.Width,
                computed.Size.Height,
                computed.Border.Left,
                computed.Padding.Left,
                computed.Padding.Top,
                computed.BorderRadius.TopLeft,
                presentation.Paint.Background,
                presentation.Paint.Foreground,
                presentation.Paint.Border,
                presentation.Paint.Opacity,
                presentation.Typography.Size.Kind == LayoutLengthKind.Logical
                    ? presentation.Typography.Size.Value
                    : 15f,
                presentation.Typography.Font.Value,
                (int)presentation.Typography.Weight,
                presentation.Typography.Slant == FontSlant.Italic,
                presentation.Typography.Wrap,
                visible,
                visible && presentation.Interaction.HitTestVisible,
                visible && presentation.Interaction.Focusable,
                (int)presentation.Interaction.Cursor,
                (int)presentation.Accessibility.Role,
                presentation.Accessibility.Name ?? text,
                presentation.Accessibility.Description ?? string.Empty,
                (int)presentation.Accessibility.Disabled,
                (int)presentation.Accessibility.ReadOnly,
                (int)presentation.Accessibility.Selected,
                (int)presentation.Accessibility.Checked,
                (int)presentation.Accessibility.Expanded,
                presentation.Accessibility.HeadingLevel,
                computed.StackIndex));
        }
        nodes.Sort(static (left, right) => left.StackIndex.CompareTo(right.StackIndex));
        var frame = new BrowserDomFrame(width, height, nodes);
        RenderFrame(JsonSerializer.Serialize(frame, BrowserDomJsonContext.Default.BrowserDomFrame));
    }

    public static string? ReadEvent() => DequeueEvent();

    public static int ViewportWidth => GetViewportWidth();

    public static int ViewportHeight => GetViewportHeight();

    [JSImport("renderFrame", "main.js")]
    private static partial void RenderFrame(string json);

    [JSImport("dequeueEvent", "main.js")]
    private static partial string? DequeueEvent();

    [JSImport("getViewportWidth", "main.js")]
    private static partial int GetViewportWidth();

    [JSImport("getViewportHeight", "main.js")]
    private static partial int GetViewportHeight();
}

internal readonly record struct BrowserDomFrame(
    int Width,
    int Height,
    IReadOnlyList<BrowserDomNode> Nodes);

internal readonly record struct BrowserDomNode(
    string Key,
    string Text,
    float X,
    float Y,
    float Width,
    float Height,
    float BorderWidth,
    float PaddingLeft,
    float PaddingTop,
    float CornerRadius,
    Color Background,
    Color Foreground,
    Color Border,
    float Opacity,
    float FontSize,
    int Font,
    int FontWeight,
    bool Italic,
    bool Wrap,
    bool Visible,
    bool HitTestVisible,
    bool Focusable,
    int Cursor,
    int Role,
    string Name,
    string Description,
    int Disabled,
    int ReadOnly,
    int Selected,
    int Checked,
    int Expanded,
    int HeadingLevel,
    int StackIndex);

[JsonSerializable(typeof(BrowserDomFrame))]
internal sealed partial class BrowserDomJsonContext : JsonSerializerContext;
#endif
