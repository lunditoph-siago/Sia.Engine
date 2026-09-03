using Sia.Graphics.Text;

namespace Sia.UI;

public readonly record struct PositionedGlyph(
    Point Position,
    GlyphAtlasInfo AtlasInfo,
    int Codepoint,
    ushort GlyphId,
    bool UsedFallback);
