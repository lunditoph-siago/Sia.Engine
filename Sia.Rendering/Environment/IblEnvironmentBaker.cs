using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public static class IblEnvironmentBaker
{
    internal static string ShaderSource()
    {
        var definitions = new Dictionary<string, string> {
            ["ENV_CUBE_SIZE"] = IblEnvironmentAsset.CubeSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ENV_MIP_COUNT"] = IblEnvironmentAsset.MipCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ENV_CUBE_TEXELS"] = IblEnvironmentAsset.CubeTexels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ENV_LUT_SIZE"] = IblEnvironmentAsset.LutSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return RenderingShaderSource.Compile(RenderingShaderSource.ReadModule("rendering/Baking/environment")!, definitions);
    }

    public static IblEnvironmentAsset Bake(in GpuFrame frame, WgpuHandle<WGPUInstance> instance, ProceduralSky sky)
    {
        ArgumentNullException.ThrowIfNull(sky);
        sky.Validate();
        using var gpu = new GpuResources(frame, 8 * 1024 * 1024);
        var skyValues = new float4[] { new(sky.Horizon, sky.Intensity), new(sky.Zenith, sky.SunExponent), new(sky.Ground, 0), new(math.normalize(sky.SunDirection), 0), new(sky.SunRadiance, 0) };
        var uniform = gpu.Upload<float4>(skyValues, WGPUBufferUsage.Uniform);
        const int count = IblEnvironmentAsset.CubeTexels + IblEnvironmentAsset.LutTexels + 9;
        const ulong bytes = count * 16ul;
        var output = gpu.Buffer(bytes, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        var readback = gpu.Buffer(bytes, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
        var layout = GpuBinding.Layout(gpu, [
            GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 80),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
        ]);
        var group = GpuBinding.Group(gpu, layout, [GpuBinding.Buffer(0, uniform), GpuBinding.Buffer(1, output)]);
        var pipelineLayout = GpuBinding.PipelineLayout(gpu, layout);
        var shader = gpu.Own(Wgpu.CreateWgslShaderModule(gpu.Device, ShaderSource(), "rendering-offline-environment"));
        var bake = Compute(gpu, pipelineLayout.GetWgpu<WGPUPipelineLayout>(), shader.GetWgpu<WGPUShaderModule>(), "bake_environment");
        var sh = Compute(gpu, pipelineLayout.GetWgpu<WGPUPipelineLayout>(), shader.GetWgpu<WGPUShaderModule>(), "bake_sh");
        var encoder = gpu.Own(Wgpu.CreateCommandEncoder(gpu.Device));
        var pass = Wgpu.BeginComputePass(encoder.GetWgpu<WGPUCommandEncoder>(), WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, bake.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, (count - 9 + 63) / 64);
            Wgpu.SetComputePipeline(pass, sh.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, 9);
        }
        finally {
            Wgpu.EndComputePass(pass);
            Wgpu.Release(ref pass);
        }
        Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), output.GetWgpu<WGPUBuffer>(), 0, readback.GetWgpu<WGPUBuffer>(), 0, bytes);
        var commands = gpu.Own(Wgpu.FinishCommandEncoder(encoder.GetWgpu<WGPUCommandEncoder>(), WGPUCommandBufferDescriptor.Default));
        Wgpu.Submit(gpu.Queue, [commands.GetWgpu<WGPUCommandBuffer>()]);
        // Offline-only synchronous readback pumps instance events with a bounded timeout.
        Wgpu.MapBufferRead(instance, readback.GetWgpu<WGPUBuffer>(), 0, bytes, TimeSpan.FromSeconds(30));
        try {
            var values = Wgpu.GetMappedRangeReadOnly<float4>(readback.GetWgpu<WGPUBuffer>(), 0, count);
            static Half[] Pack(ReadOnlySpan<float4> pixels)
            {
                var result = new Half[pixels.Length * 4];
                for (var i = 0; i < pixels.Length; i++) {
                    var v = pixels[i];
                    result[i * 4] = (Half)v.x;
                    result[(i * 4) + 1] = (Half)v.y;
                    result[(i * 4) + 2] = (Half)v.z;
                    result[(i * 4) + 3] = (Half)v.w;
                }
                return result;
            }
            return new(sky, values[^9..].ToArray(), Pack(values[..IblEnvironmentAsset.CubeTexels]), Pack(values.Slice(IblEnvironmentAsset.CubeTexels, IblEnvironmentAsset.LutTexels)));
        }
        finally {
            Wgpu.UnmapBuffer(readback.GetWgpu<WGPUBuffer>());
        }
    }

    private static unsafe Sia.Entity Compute(GpuResources gpu, WgpuHandle<WGPUPipelineLayout> layout, WgpuHandle<WGPUShaderModule> shader, string entry)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(entry);
        fixed (byte* pointer = name) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)shader.DangerousGetHandle();
            descriptor.Compute.EntryPoint = new() {
                Data = pointer,
                Length = (nuint)name.Length
            };
            return gpu.Own(Wgpu.CreateComputePipeline(gpu.Device, descriptor));
        }
    }
}
