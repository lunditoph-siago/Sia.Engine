namespace Sia.Asset;

/// <summary>Typed borrowed-stream decoding. Validate complete input before returning independently owned output.</summary>
public interface IAsyncLoadable<TSelf, in TOptions> where TSelf : class
{
    static abstract ValueTask<TSelf> LoadAsync(Stream stream, TOptions options, string? name = null,
        CancellationToken cancellationToken = default);
}
