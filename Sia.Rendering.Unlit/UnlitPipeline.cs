using System.Text;
using Sia;
using Sia.Engine.Mesh;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Unlit;

public sealed unsafe class UnlitPipeline
{
    internal Entity Pipeline { get; }

    internal Entity Layout { get; }

    private UnlitPipeline(Entity pipeline, Entity layout)
    {
        Pipeline = pipeline;
        Layout = layout;
    }

    public static UnlitPipeline Create(
        World world,
        Entity device,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        ShaderAsset shader,
        string fragmentEntryPoint)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentEntryPoint);
        var handle = device.GetWgpu<WGPUDevice>();
        var module = world.OwnWgpu(Wgpu.CreateWgslShaderModule(handle, shader.Wgsl, shader.Id.Value));
        var layout = world.OwnWgpu(CreateLayout(handle));
        var layoutHandle = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        var layoutDescriptor = WGPUPipelineLayoutDescriptor.Default;
        layoutDescriptor.BindGroupLayoutCount = 1;
        layoutDescriptor.BindGroupLayouts = &layoutHandle;
        var pipelineLayout = world.OwnWgpu(Wgpu.CreatePipelineLayout(handle, layoutDescriptor));

        Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[MeshVertexLayout.AttributeCount];
        MeshVertexLayout.Fill(attributes);
        var vertexName = "vertex"u8;
        var fragmentName = Encoding.UTF8.GetBytes(fragmentEntryPoint);
        var label = Encoding.UTF8.GetBytes($"unlit/{fragmentEntryPoint}");
        fixed (byte* vertexPointer = vertexName)
        fixed (byte* fragmentPointer = fragmentName)
        fixed (byte* labelPointer = label)
        fixed (WGPUVertexAttribute* attributePointer = attributes) {
            var vertexBuffer = WGPUVertexBufferLayout.Default;
            vertexBuffer.ArrayStride = MeshVertex.Stride;
            vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
            vertexBuffer.AttributeCount = (nuint)attributes.Length;
            vertexBuffer.Attributes = attributePointer;

            var color = WGPUColorTargetState.Default;
            color.Format = colorFormat;
            color.WriteMask = WGPUColorWriteMask.All;
            var fragment = WGPUFragmentState.Default;
            fragment.Module = (WGPUShaderModule*)module.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView { Data = fragmentPointer, Length = (nuint)fragmentName.Length };
            fragment.TargetCount = 1;
            fragment.Targets = &color;

            var depth = WGPUDepthStencilState.Default;
            depth.Format = depthFormat;
            depth.DepthWriteEnabled = WGPUOptionalBool.True;
            depth.DepthCompare = WGPUCompareFunction.Less;

            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Label = new WGPUStringView { Data = labelPointer, Length = (nuint)label.Length };
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = fragment.Module;
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexPointer, Length = (nuint)vertexName.Length };
            descriptor.Vertex.BufferCount = 1;
            descriptor.Vertex.Buffers = &vertexBuffer;
            descriptor.Fragment = &fragment;
            descriptor.DepthStencil = &depth;
            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.Back;
            descriptor.Multisample = WGPUMultisampleState.Default;
            return new UnlitPipeline(world.OwnWgpu(Wgpu.CreateRenderPipeline(handle, descriptor)), layout);
        }
    }

    private static WgpuHandle<WGPUBindGroupLayout> CreateLayout(WgpuHandle<WGPUDevice> device)
    {
        var entries = stackalloc WGPUBindGroupLayoutEntry[2];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = 0;
        entries[0].Visibility = WGPUShaderStage.Vertex;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;
        entries[0].Buffer.MinBindingSize = 64;
        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = 1;
        entries[1].Visibility = WGPUShaderStage.Vertex;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;
        entries[1].Buffer.MinBindingSize = UnlitInstance.Stride;
        var descriptor = WGPUBindGroupLayoutDescriptor.Default;
        descriptor.EntryCount = 2;
        descriptor.Entries = entries;
        return Wgpu.CreateBindGroupLayout(device, descriptor);
    }

    internal WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        UnlitViewState state)
    {
        var entries = stackalloc WGPUBindGroupEntry[2];
        entries[0] = WGPUBindGroupEntry.Default;
        entries[0].Binding = 0;
        entries[0].Buffer = (WGPUBuffer*)state.CameraBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle();
        entries[0].Size = 64;
        entries[1] = WGPUBindGroupEntry.Default;
        entries[1].Binding = 1;
        entries[1].Buffer = (WGPUBuffer*)state.InstanceBuffer.GetWgpu<WGPUBuffer>().DangerousGetHandle();
        entries[1].Size = state.InstanceCapacity;
        var descriptor = WGPUBindGroupDescriptor.Default;
        descriptor.Layout = (WGPUBindGroupLayout*)Layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        descriptor.EntryCount = 2;
        descriptor.Entries = entries;
        return Wgpu.CreateBindGroup(device, descriptor);
    }
}
