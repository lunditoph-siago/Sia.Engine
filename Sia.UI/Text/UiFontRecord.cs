using Sia.Asset;

namespace Sia.UI;

[SiaAsset]
[SiaTemplate("UiFontResource")]
public partial record UiFontRecord : AssetRecord, ILoadable<UiFontRecord>, ILoadable<UiFontRecord, int>
{
    private readonly UiFontData _data = null!;
    public required UiFontData Data {
        get => _data;
        init { ArgumentNullException.ThrowIfNull(value); _data = value; }
    }

    public static UiFontRecord Load(Stream stream, string? name = null) => new() { Data = UiFontData.Load(stream), Name = name };
    public static UiFontRecord Load(Stream stream, int maximumBytes, string? name = null) => new() { Data = UiFontData.Load(stream, maximumBytes), Name = name };
}
