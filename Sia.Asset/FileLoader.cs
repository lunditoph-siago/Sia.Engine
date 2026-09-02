namespace Sia.Asset;

public static class FileLoader
{
    public static T Load<T>(TypedPath<T> path)
        where T : ILoadable<T>
        => T.Load(File.OpenRead(path), path);

    public static T Load<T, TOptions>(TypedPath<T> path, TOptions options)
        where T : ILoadable<T, TOptions>
        => T.Load(File.OpenRead(path), options, path);
}