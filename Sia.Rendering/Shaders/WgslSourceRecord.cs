using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering;

[SiaAsset]
[SiaTemplate("WgslSourceResource")]
public partial record WgslSourceRecord : AssetRecord, ILoadable<WgslSourceRecord>, ILoadable<WgslSourceRecord, int>
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

    public static WgslSourceRecord Load(Stream stream, string? name = null) => Load(stream, MaximumBytes, name);

    public static WgslSourceRecord Load(Stream stream, int maximumBytes, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        try { return new() { Source = Utf8Text.Read(stream, maximumBytes), Name = name }; }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid UTF-8 WGSL source.", error); }
    }
}
