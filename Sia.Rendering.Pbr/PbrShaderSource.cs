using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using System.Reflection;
using Sia.Graphics.Wgsl;

namespace Sia.Engine.Rendering.Pbr;

public static class PbrShaderSource
{
    private const string k_ResourcePrefix = "Sia.Rendering.Pbr.Shaders.";

    private static readonly string[] s_ModuleResourceNames = [
        k_ResourcePrefix + "scene_common.wgsl",
        k_ResourcePrefix + "clustered_forward.wgsl",
        k_ResourcePrefix + "pbr_lighting.wgsl",
        k_ResourcePrefix + "shadows.wgsl",
        k_ResourcePrefix + "ibl.wgsl",
        k_ResourcePrefix + "atmosphere.wgsl",
        k_ResourcePrefix + "visibility_geometry.wgsl",
        k_ResourcePrefix + "scene_lighting.wgsl",
    ];

    public static string LoadDepthPrepass() => Load(k_ResourcePrefix + "depth_prepass.wgsl");

    public static string LoadForwardPbr() => Load(k_ResourcePrefix + "forward_pbr.wgsl");

    public static string LoadClusterLightCulling() => Load(k_ResourcePrefix + "cluster_light_culling.wgsl");

    public static string LoadShadowDepth() => Load(k_ResourcePrefix + "shadow_depth.wgsl");

    public static string LoadIblPrefilterSpecular() => Load(k_ResourcePrefix + "ibl_prefilter_specular.wgsl");

    public static string LoadIblBrdfLut() => Load(k_ResourcePrefix + "ibl_brdf_lut.wgsl");

    public static string LoadSkybox() => Load(k_ResourcePrefix + "skybox.wgsl");

    public static string LoadToneMapping() => Load(k_ResourcePrefix + "tone_mapping.wgsl");

    internal static string LoadVisibilityRaster() => Load(k_ResourcePrefix + "visibility_raster.wgsl");

    internal static string LoadVisibilityResolve() => Load(k_ResourcePrefix + "visibility_resolve.wgsl");
    internal static string LoadVisibilityLighting() => Load(k_ResourcePrefix + "visibility_lighting.wgsl");

    internal static string LoadVisibilityLod() => Load(k_ResourcePrefix + "visibility_lod.wgsl");

    internal static string LoadVisibilityHzb() => Load(k_ResourcePrefix + "visibility_hzb.wgsl");

    internal static string LoadVisibilityCompact() => Load(k_ResourcePrefix + "visibility_compact.wgsl");

    internal static string LoadAtmosphere(string name) => Load(k_ResourcePrefix + "atmosphere_" + name + ".wgsl");

    private static string Load(string entryResourceName)
    {
        var registry = BuildModuleRegistry();
        var entrySource = ReadResource(entryResourceName);
        var result = WgslPreprocessor.Process(
            entrySource, null, (importPath, _) =>
                registry.TryGetValue(importPath, out var source) ? source : null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                $"Failed to process '{entryResourceName}': " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }

    private static Dictionary<string, string> BuildModuleRegistry()
    {
        var registry = new Dictionary<string, string>();
        foreach (var resourceName in s_ModuleResourceNames) {
            var content = ReadResource(resourceName);
            var directives = WgslDirectiveParser.Parse(content);
            if (directives.ImportPath is { } importPath) {
                registry[importPath] = content;
            }
        }
        return registry;
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(PbrShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded WGSL resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
