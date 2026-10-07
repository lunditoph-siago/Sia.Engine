using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class ReflectionCompositionTests
{
    [Theory]
    [InlineData(RenderQuality.Low, false)]
    [InlineData(RenderQuality.Medium, false)]
    [InlineData(RenderQuality.High, true)]
    public void OnlyHighRequestsSceneReflections(RenderQuality quality, bool enabled)
    {
        var settings = PbrRendererSettings.ForQuality(quality);
        Assert.Equal(enabled, settings.SceneReflections);
        Assert.Equal(enabled, settings.TemporalReflections);
    }

    [Theory]
    [InlineData(RenderQuality.Low, false)]
    [InlineData(RenderQuality.Medium, false)]
    [InlineData(RenderQuality.High, true)]
    public void StaticReceiverIdentityUsesTheExistingInstancePathOnlyForHigh(RenderQuality quality, bool instanced)
    {
        var scene = PbrSceneAsset.Create([], [], []);
        var configuration = PbrPipelineConfiguration.Create(scene, null, PbrRendererSettings.ForQuality(quality));
        Assert.Equal(instanced, configuration.LocalInstances);
        Assert.Equal(instanced, configuration.Shader.LocalInstances);
    }

    [Fact]
    public void ShaderLinksBoundedSceneTraversalWithoutGeometryBindings()
    {
        var source = PbrShaderSource.Compile("reflections.wgsl", new PbrShaderOptions { SceneReflections = true });
        Assert.Contains("maximum_visits", source);
        Assert.Contains("hit.complete", source);
        Assert.DoesNotContain("visible_work:", source);
        Assert.DoesNotContain("topology:", source);
        Assert.DoesNotContain("texture_depth_2d", source);
        Assert.DoesNotContain("dynamic_tracing:", source);
        var transparent = PbrShaderSource.Compile("reflections.wgsl", new PbrShaderOptions { SceneReflections = true, LocalInstances = true });
        Assert.DoesNotContain("dynamic_tracing:", transparent);
        var actors = PbrShaderSource.Compile("reflections.wgsl", new PbrShaderOptions { SceneReflections = true, DynamicTrace = true });
        Assert.Contains("dynamic_tracing:", actors);
    }
}
