using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Debug;

internal sealed unsafe class DebugPipeline
{
    private DebugPipeline(Entity renderPipeline, Entity bindGroupLayout)
    {
        RenderPipeline = renderPipeline;
        BindGroupLayout = bindGroupLayout;
    }

    public Entity RenderPipeline { get; }

    public Entity BindGroupLayout { get; }

    public static DebugPipeline Create(
        World world,
        Entity device,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shader = world.OwnWgpu(Wgpu.CreateWgslShaderModule(
            deviceHandle,
            DebugShaderSource.Load(),
            "debug-overlay"));
        var bindGroupLayout = world.OwnWgpu(CreateBindGroupLayout(deviceHandle));
        var pipelineLayout = world.OwnWgpu(CreatePipelineLayout(
            deviceHandle,
            bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shader.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            colorFormat,
            depthFormat));
        return new DebugPipeline(renderPipeline, bindGroupLayout);
    }

    public WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBuffer> cameraBuffer)
    {
        var entry = WGPUBindGroupEntry.Default;
        entry.Binding = 0;
        entry.Buffer = Pointer(cameraBuffer);
        entry.Size = DebugCameraUniformData.Stride;

        var descriptor = WGPUBindGroupDescriptor.Default;
        descriptor.Layout = Pointer(BindGroupLayout.GetWgpu<WGPUBindGroupLayout>());
        descriptor.EntryCount = 1;
        descriptor.Entries = &entry;
        return Wgpu.CreateBindGroup(device, in descriptor);
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateBindGroupLayout(
        WgpuHandle<WGPUDevice> device)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = 0;
        entry.Visibility = WGPUShaderStage.Vertex;
        entry.Buffer.Type = WGPUBufferBindingType.Uniform;
        entry.Buffer.MinBindingSize = DebugCameraUniformData.Stride;

        var descriptor = WGPUBindGroupLayoutDescriptor.Default;
        descriptor.EntryCount = 1;
        descriptor.Entries = &entry;
        return Wgpu.CreateBindGroupLayout(device, in descriptor);
    }

    private static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> bindGroupLayout)
    {
        var layout = Pointer(bindGroupLayout);
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &layout;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shader,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat)
    {
        var vertexEntryPoint = "vertex"u8;
        var fragmentEntryPoint = "fragment"u8;
        fixed (byte* vertexEntry = vertexEntryPoint)
        fixed (byte* fragmentEntry = fragmentEntryPoint) {
            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[3];
            attributes[0] = WGPUVertexAttribute.Default;
            attributes[0].Format = WGPUVertexFormat.Float32x3;
            attributes[0].Offset = DebugVertex.PositionOffset;
            attributes[0].ShaderLocation = 0;
            attributes[1] = WGPUVertexAttribute.Default;
            attributes[1].Format = WGPUVertexFormat.Float32x3;
            attributes[1].Offset = DebugVertex.NormalOffset;
            attributes[1].ShaderLocation = 1;
            attributes[2] = WGPUVertexAttribute.Default;
            attributes[2].Format = WGPUVertexFormat.Float32x4;
            attributes[2].Offset = DebugVertex.ColorOffset;
            attributes[2].ShaderLocation = 2;

            fixed (WGPUVertexAttribute* attributePointer = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.ArrayStride = DebugVertex.Stride;
                vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
                vertexBuffer.AttributeCount = (nuint)attributes.Length;
                vertexBuffer.Attributes = attributePointer;

                var colorTarget = WGPUColorTargetState.Default;
                colorTarget.Format = colorFormat;
                colorTarget.WriteMask = WGPUColorWriteMask.All;

                var fragment = WGPUFragmentState.Default;
                fragment.Module = Pointer(shader);
                fragment.EntryPoint = new WGPUStringView {
                    Data = fragmentEntry,
                    Length = (nuint)fragmentEntryPoint.Length,
                };
                fragment.TargetCount = 1;
                fragment.Targets = &colorTarget;

                var depthStencil = WGPUDepthStencilState.Default;
                depthStencil.Format = depthFormat;
                depthStencil.DepthWriteEnabled = WGPUOptionalBool.True;
                depthStencil.DepthCompare = WGPUCompareFunction.LessEqual;

                var descriptor = WGPURenderPipelineDescriptor.Default;
                descriptor.Layout = Pointer(pipelineLayout);
                descriptor.Vertex = WGPUVertexState.Default;
                descriptor.Vertex.Module = Pointer(shader);
                descriptor.Vertex.EntryPoint = new WGPUStringView {
                    Data = vertexEntry,
                    Length = (nuint)vertexEntryPoint.Length,
                };
                descriptor.Vertex.BufferCount = 1;
                descriptor.Vertex.Buffers = &vertexBuffer;
                descriptor.Primitive = WGPUPrimitiveState.Default;
                descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
                descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
                descriptor.Primitive.CullMode = WGPUCullMode.None;
                descriptor.DepthStencil = &depthStencil;
                descriptor.Multisample = WGPUMultisampleState.Default;
                descriptor.Fragment = &fragment;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }

    private static T* Pointer<T>(WgpuHandle<T> handle)
        where T : unmanaged => (T*)handle.DangerousGetHandle();
}
