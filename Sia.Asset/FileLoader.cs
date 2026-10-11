namespace Sia.Asset;

public static class FileLoader
{
    public static T Load<T>(TypedPath<T> path)
        where T : ILoadable<T>
    {
        using var stream = File.OpenRead(path);
        return T.Load(stream, path);
    }

    public static T Load<T, TOptions>(TypedPath<T> path, TOptions options)
        where T : ILoadable<T, TOptions>
    {
        using var stream = File.OpenRead(path);
        return T.Load(stream, options, path);
    }
}
