using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed unsafe class PbrDepthPrepassPipeline
{
    internal Entity RenderPipeline { get; }
    internal Entity BindGroupLayout { get; }

    private PbrDepthPrepassPipeline(Entity renderPipeline, Entity bindGroupLayout)
    {
        RenderPipeline = renderPipeline;
        BindGroupLayout = bindGroupLayout;
    }

    public static PbrDepthPrepassPipeline Create(World world, Entity device, WGPUTextureFormat depthFormat)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, PbrShaderSource.LoadDepthPrepass(), "depth_prepass"));
        var bindGroupLayout = world.OwnWgpu(PbrObjectBindGroupLayout.Create(
            deviceHandle, instanceVisibility: WGPUShaderStage.Vertex));
        var pipelineLayout = world.OwnWgpu(PbrObjectBindGroupLayout.CreatePipelineLayout(
            deviceHandle, bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            depthFormat));
        return new PbrDepthPrepassPipeline(renderPipeline, bindGroupLayout);
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat depthFormat)
    {
        var vertexEntryPoint = "vertex"u8;
        fixed (byte* vertexEntry = vertexEntryPoint) {
            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[MeshVertexLayout.AttributeCount];
            MeshVertexLayout.Fill(attributes);

            fixed (WGPUVertexAttribute* attributesPtr = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
                vertexBuffer.ArrayStride = MeshVertex.Stride;
                vertexBuffer.AttributeCount = (nuint)attributes.Length;
                vertexBuffer.Attributes = attributesPtr;

                var depthStencil = WGPUDepthStencilState.Default;
                depthStencil.Format = depthFormat;
                depthStencil.DepthWriteEnabled = WGPUOptionalBool.True;
                depthStencil.DepthCompare = WGPUCompareFunction.Less;

                var descriptor = WGPURenderPipelineDescriptor.Default;
                descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
                descriptor.Vertex = WGPUVertexState.Default;
                descriptor.Vertex.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
                descriptor.Vertex.EntryPoint = new WGPUStringView {
                    Data = vertexEntry,
                    Length = (nuint)vertexEntryPoint.Length
                };
                descriptor.Vertex.BufferCount = 1;
                descriptor.Vertex.Buffers = &vertexBuffer;
                descriptor.Primitive = WGPUPrimitiveState.Default;
                descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
                descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
                descriptor.Primitive.CullMode = WGPUCullMode.Back;
                descriptor.DepthStencil = &depthStencil;
                descriptor.Multisample = WGPUMultisampleState.Default;
                descriptor.Fragment = null;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }
}
