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
        => T.Load(GetStream(path, assembly), path);

    public static T Load<T, TOptions>(TypedPath<T> path, TOptions options, Assembly assembly)
        where T : ILoadable<T, TOptions>
        => T.Load(GetStream(path, assembly), options, path);

    public static T LoadInternal<T>(TypedPath<T> path)
        where T : ILoadable<T>
        => LoadInternal(path, Assembly.GetCallingAssembly());

    public static T LoadInternal<T, TOptions>(TypedPath<T> path, TOptions options)
        where T : ILoadable<T, TOptions>
        => LoadInternal(path, options, Assembly.GetCallingAssembly());
    
    public static T LoadInternal<T>(TypedPath<T> path, Assembly assembly)
        where T : ILoadable<T>
        => T.Load(GetStream(GetInternalName(path, assembly), assembly), path);

    public static T LoadInternal<T, TOptions>(TypedPath<T> path, TOptions options, Assembly assembly)
        where T : ILoadable<T, TOptions>
        => T.Load(GetStream(GetInternalName(path, assembly), assembly), options, path);

    private static string GetInternalName(string path, Assembly assembly)
        => assembly.FullName![0..assembly.FullName!.IndexOf(',')] + ".Embedded." + path;

    private static Stream GetStream(string path, Assembly assembly)
        => assembly.GetManifestResourceStream(path)
            ?? throw new FileNotFoundException("Asset not found: " + path);
}