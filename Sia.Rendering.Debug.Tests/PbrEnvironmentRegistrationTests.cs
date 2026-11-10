using System.Reflection;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrEnvironmentRegistrationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T CpuOwner<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set(object owner, string name, object value)
        => (owner.GetType().GetField(name, Instance)
            ?? owner.GetType().GetField("<" + name + ">k__BackingField", Instance))!.SetValue(owner, value);

    [Fact]
    public void MaterialTextureRevisionsKeepTheEnvironmentDependencyRegistered()
    {
        // Bypass only GPU construction. RefreshSceneTextures, ReadProbeTrace and
        // every graph registration/compilation below are the production methods.
        using var world = new World();
        using var registry = new WgpuRenderGraphRegistry();
        var cube = world.Create(); var lut = world.Create();
        var environment = CpuOwner<IblEnvironmentGpu>();
        Set(environment, "_cube", cube); Set(environment, "_lut", lut);
        var materials = CpuOwner<PbrMaterials>();
        var materialTextures = new List<Entity>();
        Set(materials, "Textures", materialTextures);
        var owner = CpuOwner<PbrRenderer>();
        Set(owner, "Materials", materials); Set(owner, "Environment", environment);
        Set(owner, "Settings", PbrRendererSettings.ForQuality(RenderQuality.High) with { BakedEnvironment = CpuOwner<IblEnvironmentAsset>() });
        Set(owner, "Probes", CpuOwner<DiffuseProbeGpu>());

        var view = CpuOwner<PbrView>();
        var cubeKey = new RenderGraphTextureKey("pbr/main/environment-cube");
        var traceKey = new RenderGraphBufferKey("pbr/main/trace");
        var textures = new List<(RenderGraphTextureKey Key, Entity Value)>();
        Set(view, "_owner", owner); Set(view, "_prefix", "pbr/main/");
        Set(view, "_environmentCubeKey", cubeKey); Set(view, "_traceKey", traceKey);
        Set(view, "_sceneTextures", textures);
        var refresh = typeof(PbrView).GetMethod("RefreshSceneTextures", Instance)!;
        var readTrace = typeof(PbrView).GetMethod("ReadProbeTrace", Instance)!;
        using var trace = registry.RegisterImportedBuffer(traceKey,
            new("trace", 16, RenderGraphBufferUsage.Storage));
        using var pass = registry.RegisterPass(new("probe-update"), "probe-update",
            declaration => ((RenderGraphPassDeclarationBuilder)readTrace.Invoke(view, [declaration])!).MarkSideEffect(),
            RenderGraphPassKind.Compute);
        var registrations = new List<RenderGraphRegistration>();
        try {
            // A published mip can replace an array and change the number of
            // material texture resources. Both keep the environment key stable.
            foreach (var count in new[] { 1, 3, 0, 2 }) {
                materialTextures.Clear();
                for (var i = 0; i < count; i++) materialTextures.Add(world.Create());
                var revision = materials.Revision + 1;
                typeof(PbrMaterials).GetProperty(nameof(PbrMaterials.Revision))!.SetValue(materials, revision);
                refresh.Invoke(view, null);
                Assert.Equal(count + 2, textures.Count);
                Assert.Equal(textures.Count, textures.Select(texture => texture.Key).Distinct().Count());
                Assert.Equal(cube, Assert.Single(textures, texture => texture.Key == cubeKey).Value);
                Assert.Equal(1, textures.Count(texture => texture.Value == lut));
                foreach (var texture in materialTextures) Assert.Contains(textures, item => item.Value == texture);

                foreach (var registration in registrations) registration.Dispose();
                registrations.Clear();
                foreach (var (key, _) in textures)
                    registrations.Add(registry.RegisterImportedTexture(key,
                        new(key.ToString(), RenderGraphTextureFormat.RGBA16Float, 8, 8,
                            depthOrArrayLayers: key == cubeKey ? 6u : 1u,
                            usage: RenderGraphTextureUsage.TextureBinding)));
                // Resolves the actual High probe/environment dependency through
                // the real graph compiler after the previous imports are removed.
                Assert.NotNull(registry.PreparePlan());
            }
            Assert.Equal(4, registry.CompilationCount);
        }
        finally { foreach (var registration in registrations) registration.Dispose(); }
    }
}
