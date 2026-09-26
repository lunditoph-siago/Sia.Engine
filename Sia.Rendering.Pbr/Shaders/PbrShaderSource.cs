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
        k_ResourcePrefix + "pbr_lighting.wgsl",
        k_ResourcePrefix + "shadows.wgsl",
        k_ResourcePrefix + "ibl.wgsl",
        k_ResourcePrefix + "atmosphere.wgsl",
        k_ResourcePrefix + "visibility_geometry.wgsl",
        k_ResourcePrefix + "visibility_occlusion.wgsl",
        k_ResourcePrefix + "visibility_materials.wgsl",
        k_ResourcePrefix + "scene_lighting.wgsl",
        k_ResourcePrefix + "screen_trace.wgsl",
    ];

    internal static string LoadTransparentPbr(bool transmission = false) => Load(k_ResourcePrefix + "transparent_pbr.wgsl",
        transmission ? new Dictionary<string, string> { ["OPTICAL_TRANSMISSION"] = "true" } : null);
    internal static string LoadScreenLighting(bool gather, bool reflections, bool indirect)
    {
        var definitions = new Dictionary<string, string>();
        if (gather) { definitions["SCREEN_GATHER"] = "true"; }
        if (reflections) { definitions["SCREEN_REFLECTIONS"] = "true"; }
        if (indirect) { definitions["SCREEN_INDIRECT"] = "true"; }
        return Load(k_ResourcePrefix + "screen_lighting.wgsl", definitions);
    }


    public static string LoadIblPrefilterSpecular() => Load(k_ResourcePrefix + "ibl_prefilter_specular.wgsl");

    public static string LoadIblBrdfLut() => Load(k_ResourcePrefix + "ibl_brdf_lut.wgsl");

    public static string LoadSkybox() => Load(k_ResourcePrefix + "skybox.wgsl");

    public static string LoadToneMapping() => Load(k_ResourcePrefix + "tone_mapping.wgsl");

    internal static string LoadVisibilityRaster(bool worldSpace = false) => LoadGeometry("visibility_raster", worldSpace);

    internal static string LoadVisibilityResolve(bool worldSpace = false) => LoadGeometry("visibility_resolve", worldSpace);
    internal static string LoadFusedLighting(bool worldSpace) {
        var definitions = new Dictionary<string, string> { ["FUSED_LIGHTING"] = "true" };
        if (worldSpace) definitions["WORLD_SPACE_GEOMETRY"] = "true";
        return Load(k_ResourcePrefix + "visibility_resolve.wgsl", definitions);
    }
    internal static string LoadVisibilityFlat() => Load(k_ResourcePrefix + "visibility_flat.wgsl");
    internal static string LoadVisibilityMaterialTiles() => Load(k_ResourcePrefix + "visibility_material_tiles.wgsl");
    internal static string LoadVisibilityLighting() => Load(k_ResourcePrefix + "visibility_lighting.wgsl");

    internal static string LoadVisibilityLod() => Load(k_ResourcePrefix + "visibility_lod.wgsl");

    internal static string LoadVisibilityHzb() => Load(k_ResourcePrefix + "visibility_hzb.wgsl");

    internal static string LoadVisibilityCompact() => Load(k_ResourcePrefix + "visibility_compact.wgsl");
    internal static string LoadVisibilityClusters(bool worldSpace = false) => LoadGeometry("visibility_clusters", worldSpace);
    internal static string LoadVisibilityShadowIndices() => Load(k_ResourcePrefix + "visibility_shadow_indices.wgsl");

    internal static string LoadAtmosphere(string name) => Load(k_ResourcePrefix + "atmosphere_" + name + ".wgsl");

    private static string LoadGeometry(string name, bool worldSpace) => Load(k_ResourcePrefix + name + ".wgsl",
        worldSpace ? new Dictionary<string, string> { ["WORLD_SPACE_GEOMETRY"] = "true" } : null);

    private static string Load(string entryResourceName, IReadOnlyDictionary<string, string>? definitions = null)
    {
        var registry = BuildModuleRegistry();
        if (definitions?.ContainsKey("FUSED_LIGHTING") == true) {
            registry["pbr::scene_lighting"] = registry["pbr::scene_lighting"]
                .Replace("@group(2)", "@group(3)").Replace("@group(1)", "@group(2)");
        }
        var entrySource = ReadResource(entryResourceName);
        var result = WgslPreprocessor.Process(
            entrySource, definitions, (importPath, _) =>
                registry.TryGetValue(importPath, out var source) ? source : null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                $"Failed to process '{entryResourceName}': " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }

    private static Dictionary<string, string> BuildModuleRegistry()
    {
        var registry = new Dictionary<string, string> { ["rendering::clustered_forward"] = LightingShaderSource.ClusteredForwardModule };
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
