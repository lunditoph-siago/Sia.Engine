namespace Sia.Asset;

public interface ILoadable<T>
    where T : ILoadable<T>
{
    /// <summary>Decode from a borrowed stream; do not retain it after returning.</summary>
    static abstract T Load(Stream stream, string? name = null);
}

public interface ILoadable<T, TOptions>
    where T : ILoadable<T, TOptions>
{
    /// <summary>Decode from a borrowed stream; do not retain it after returning.</summary>
    static abstract T Load(Stream stream, TOptions options, string? name = null);
}
