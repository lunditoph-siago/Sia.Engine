using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct ShowcaseTheme(
    Color Canvas,
    Color Surface,
    Color RaisedSurface,
    Color Foreground,
    Color Muted,
    Color Border,
    Color Accent,
    Color AccentStrong,
    Color Hover,
    Color Pressed,
    Color Focus)
{
    public static readonly ShowcaseTheme Dark = new(
        new(0.025f, 0.03f, 0.045f, 1f),
        new(0.055f, 0.068f, 0.10f, 1f),
        new(0.085f, 0.105f, 0.15f, 1f),
        new(0.94f, 0.96f, 1f, 1f),
        new(0.60f, 0.66f, 0.76f, 1f),
        new(0.17f, 0.21f, 0.30f, 1f),
        new(0.28f, 0.54f, 0.98f, 1f),
        new(0.16f, 0.39f, 0.85f, 1f),
        new(0.13f, 0.18f, 0.28f, 1f),
        new(0.10f, 0.28f, 0.58f, 1f),
        new(0.47f, 0.72f, 1f, 1f));

    public static readonly ShowcaseTheme Light = new(
        new(0.90f, 0.93f, 0.97f, 1f),
        new(0.98f, 0.99f, 1f, 1f),
        new(0.94f, 0.96f, 1f, 1f),
        new(0.08f, 0.11f, 0.17f, 1f),
        new(0.34f, 0.39f, 0.48f, 1f),
        new(0.72f, 0.77f, 0.84f, 1f),
        new(0.12f, 0.42f, 0.90f, 1f),
        new(0.07f, 0.31f, 0.74f, 1f),
        new(0.85f, 0.90f, 0.98f, 1f),
        new(0.68f, 0.79f, 0.97f, 1f),
        new(0.08f, 0.38f, 0.88f, 1f));
}
