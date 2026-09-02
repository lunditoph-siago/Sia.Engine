namespace Sia.Asset;

public interface ILoadable<T>
    where T : ILoadable<T>
{
    static abstract T Load(Stream stream, string? name = null);
}

public interface ILoadable<T, TOptions>
    where T : ILoadable<T, TOptions>
{
    static abstract T Load(Stream stream, TOptions options, string? name = null);
}