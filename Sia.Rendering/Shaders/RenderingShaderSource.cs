using Sia.Graphics.Wgsl;

namespace Sia.Engine.Rendering;

public static partial class RenderingShaderSource
{
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

}
