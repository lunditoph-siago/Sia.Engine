using System.Reflection;
using Sia.Graphics.Wgsl;

namespace Sia.Engine.Rendering;

public static class RenderingShaderSource
{
    public static string Compile(string source, IReadOnlyDictionary<string, string>? definitions = null,
        WgslImportResolver? resolveImport = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var result = WgslPreprocessor.Process(source, definitions, (module, context) =>
            module.StartsWith("rendering/", StringComparison.Ordinal)
                ? ReadModule(module)
                : resolveImport?.Invoke(module, context));
        if (result.HasErrors)
            throw new InvalidOperationException($"Rendering shader: {string.Join("\n", result.Diagnostics)}");
        return result.CombinedSource;
    }

    public static string? ReadModule(string module)
        => ReadEmbeddedModule(typeof(RenderingShaderSource).Assembly,
            "rendering/", "Sia.Rendering.Shaders.", module);

    public static string? ReadEmbeddedModule(Assembly assembly, string modulePrefix, string resourcePrefix, string module)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentNullException.ThrowIfNull(module);
        if (!module.StartsWith(modulePrefix, StringComparison.Ordinal)) return null;
        var name = resourcePrefix + module[modulePrefix.Length..].Replace('/', '.') + ".wgsl";
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
