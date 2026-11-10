using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrPipelines
{
    internal Entity AlphaShadowDrawLayout { get; private set; }
    internal Entity AlphaShadowGateLayout { get; private set; }
    internal Entity AlphaShadowGate { get; private set; }
    internal Entity AlphaShadow { get; private set; }
    internal Entity AlphaShadowDouble { get; private set; }

    private void InitializeAlphaShadowPipelines(bool gpuStream, bool core)
    {
        var draw = new WGPUBindGroupLayoutEntry { Binding = 0, Visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment,
            Buffer = new() { Type = WGPUBufferBindingType.Uniform, MinBindingSize = 16, HasDynamicOffset = 1 } };
        AlphaShadowDrawLayout = GpuBinding.Layout(_gpu, [draw,
            GpuBinding.Buffer(1, WGPUBufferBindingType.Uniform, WGPUShaderStage.Vertex, 16)]);
        AlphaShadowGateLayout = GpuBinding.Layout(_gpu, [
            GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute, 16)]);
        AlphaShadowGate = Compute(Module("shadow_coverage_gate.wgsl"),
            GpuBinding.PipelineLayout(_gpu, AlphaShadowGateLayout), "gate_shadow_coverage");
        var layout = GpuBinding.PipelineLayout(_gpu, RasterFrameLayout, GeometryLayout, MaterialLayout, AlphaShadowDrawLayout);
        var shader = Module("shadow_coverage.wgsl", streamInstances: gpuStream);
        var fragment = core ? "alpha_shadow_fragment" : "alpha_shadow_fragment_depth";
        AlphaShadow = Render(shader, layout, "alpha_shadow_vertex", fragment, null, true, WGPUCullMode.Back, shadowBias: true);
        AlphaShadowDouble = Render(shader, layout, "alpha_shadow_vertex", fragment, null, true, WGPUCullMode.None, shadowBias: true);
    }
}
