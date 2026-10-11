using System.Reflection;
using Sia.Asset;
using Sia.Graphics.Wgsl;

namespace Sia.Engine.Rendering;

public static partial class RenderingShaderSource
{
    public static string Compile(string source, IReadOnlyDictionary<string, string>? definitions = null,
        WgslImportResolver? resolveImport = null)
        => CompileResolved(source, (module, context) => module.StartsWith("rendering/", StringComparison.Ordinal)
            ? ReadModule(module) : resolveImport?.Invoke(module, context), definitions);

    // Stateless preprocessing with explicit caller lookup; no builtin IO/acquisition fallback.
    public static string CompileResolved(string source, WgslImportResolver resolveImport,
        IReadOnlyDictionary<string, string>? definitions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(resolveImport);
        var result = WgslPreprocessor.Process(source, definitions, resolveImport);
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
        if (assembly.GetManifestResourceInfo(name) is null) return null;
        return EmbeddedLoader.Load<WgslSourceRecord>(name, assembly).Source;
    }
}
