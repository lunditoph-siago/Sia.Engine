namespace Sia.Engine.Rendering.Pbr;

// Integer lightmap texture copies share one row splitter. Elements are either
// scalar map entries, uint4 metadata texels or coefficient pairs of uint4 texels.
internal readonly record struct PbrLightmapTextureCopy(int X, int Y, int Width, int Height,
    int ElementCount, int WordCount, int BytesPerRow)
{
    internal static PbrLightmapTextureCopy Next(int rowElements, int pixelsPerElement, int wordsPerPixel,
        int firstElement, int remainingElements)
    {
        if (rowElements < 1 || pixelsPerElement is < 1 or > 2 || wordsPerPixel is not (1 or 4)
            || firstElement < 0 || remainingElements < 1 || firstElement > int.MaxValue - remainingElements)
            throw new ArgumentOutOfRangeException(nameof(remainingElements), "Invalid integer texture copy range.");
        var x = firstElement % rowElements;
        var wholeRows = x == 0 && remainingElements >= rowElements;
        var columns = wholeRows ? rowElements : System.Math.Min(rowElements - x, remainingElements);
        var rows = wholeRows ? remainingElements / rowElements : 1;
        var elements = checked(columns * rows);
        var width = checked(columns * pixelsPerElement);
        return new(checked(x * pixelsPerElement), firstElement / rowElements, width, rows, elements,
            checked(elements * pixelsPerElement * wordsPerPixel), checked(width * wordsPerPixel * sizeof(uint)));
    }
}
