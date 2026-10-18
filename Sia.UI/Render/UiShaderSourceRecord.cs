using Sia.Asset;

namespace Sia.UI;

[SiaAsset]
[SiaTemplate("UiShaderSourceResource")]
public partial record UiShaderSourceRecord : AssetRecord, ILoadable<UiShaderSourceRecord>, ILoadable<UiShaderSourceRecord, int>
{
    public const int MaximumBytes = 1024 * 1024;
    private readonly string _source = "";

    public required string Source {
        get => _source;
        init {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Utf8Text.Validate(value, MaximumBytes);
            _source = value;
        }
    }

    public static UiShaderSourceRecord Load(Stream stream, string? name = null) => Load(stream, MaximumBytes, name);

    public static UiShaderSourceRecord Load(Stream stream, int maximumBytes, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        try { return new() { Source = Utf8Text.Read(stream, maximumBytes), Name = name }; }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid UTF-8 UI shader source.", error); }
    }
}
