using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private void InitializeVisibility()
    {
        var mesh = ProceduralMesh.Sphere(0.48f, 48, 96);
        var clusters = MeshletBuilder.Build(mesh);
        var instances = new List<VisibilityInstance>();
        for (var z = -3; z <= 3; z++) {
            for (var x = -3; x <= 3; x++) {
                var transform = float4x4.TRS(new float3(x * 1.2f, 0, z * 1.2f), quaternion.identity,
                    new float3(1, 1 + (x + 3) * 0.1f, 1));
                var material = PbrMaterial.Default with {
                    BaseColor = new float3(0.9f, 0.65f, 0.25f),
                    Metallic = (x + 3) / 6f,
                    Roughness = 0.1f + (z + 3) / 6f * 0.8f
                };
                instances.Add(new(transform, material));
            }
        }
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var mode = _pipeline switch {
            ScenePipeline.VisibilityNormals => VisibilityDebugMode.Normals,
            ScenePipeline.VisibilityUV => VisibilityDebugMode.UV,
            _ => VisibilityDebugMode.Shaded
        };
        var feature = VisibilityPbrFeature.Create(in frame, MeshletRasterData.Create(mesh, clusters),
            instances.ToArray(), CreateVisibilityChecker(), _surfaceFormat, mode);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(feature).Build();
        Console.WriteLine($"Visibility: {clusters.Meshlets.Length} meshlets, {feature.InstanceCount} instances, "
            + $"{feature.TriangleCapacity} triangles, one indirect geometry draw.");
    }

    private static VisibilityAlbedo CreateVisibilityChecker()
    {
        var levels = new List<ReadOnlyMemory<byte>>();
        for (var size = 128; size >= 1; size /= 2) {
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var value = size < 16 ? (byte)153
                        : ((x / (size / 16) + y / (size / 16)) & 1) == 0 ? (byte)255 : (byte)51;
                    var offset = (y * size + x) * 4;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }
            levels.Add(pixels);
        }
        return new(128, 128, levels.ToArray());
    }
}
