namespace Sia.Asset;

using System.Reflection;

public static class EmbeddedLoader
{
    public static T Load<T>(TypedPath<T> path)
        where T : ILoadable<T>
        => Load(path, Assembly.GetCallingAssembly());

    public static T Load<T, TOptions>(TypedPath<T> path, TOptions options)
        where T : ILoadable<T, TOptions>
        => Load(path, options, Assembly.GetCallingAssembly());
    
    public static T Load<T>(TypedPath<T> path, Assembly assembly)
        where T : ILoadable<T>
    {
        using var stream = GetStream(path, assembly);
        return T.Load(stream, path);
    }

    public static T Load<T, TOptions>(TypedPath<T> path, TOptions options, Assembly assembly)
        where T : ILoadable<T, TOptions>
    {
        using var stream = GetStream(path, assembly);
        return T.Load(stream, options, path);
    }

    public static T LoadInternal<T>(TypedPath<T> path)
        where T : ILoadable<T>
        => LoadInternal(path, Assembly.GetCallingAssembly());

    public static T LoadInternal<T, TOptions>(TypedPath<T> path, TOptions options)
        where T : ILoadable<T, TOptions>
        => LoadInternal(path, options, Assembly.GetCallingAssembly());
    
    public static T LoadInternal<T>(TypedPath<T> path, Assembly assembly)
        where T : ILoadable<T>
    {
        using var stream = GetStream(GetInternalName(path, assembly), assembly);
        return T.Load(stream, path);
    }

    public static T LoadInternal<T, TOptions>(TypedPath<T> path, TOptions options, Assembly assembly)
        where T : ILoadable<T, TOptions>
    {
        using var stream = GetStream(GetInternalName(path, assembly), assembly);
        return T.Load(stream, options, path);
    }

    private static string GetInternalName(string path, Assembly assembly)
        => assembly.FullName![0..assembly.FullName!.IndexOf(',')] + ".Embedded." + path;

    private static Stream GetStream(string path, Assembly assembly)
        => assembly.GetManifestResourceStream(path)
            ?? throw new FileNotFoundException("Asset not found: " + path);
}
