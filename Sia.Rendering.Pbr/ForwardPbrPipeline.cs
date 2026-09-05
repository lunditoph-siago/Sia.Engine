using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using System.Text;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed unsafe class ForwardPbrPipeline
{
    internal Entity RenderPipeline { get; }
    internal Entity BindGroupLayout { get; }
    internal Entity LightingBindGroupLayout { get; }
    internal Entity IblBindGroupLayout { get; }

    private ForwardPbrPipeline(
        Entity renderPipeline, Entity bindGroupLayout, Entity lightingBindGroupLayout, Entity iblBindGroupLayout)
    {
        RenderPipeline = renderPipeline;
        BindGroupLayout = bindGroupLayout;
        LightingBindGroupLayout = lightingBindGroupLayout;
        IblBindGroupLayout = iblBindGroupLayout;
    }

    public static ForwardPbrPipeline Create(
        World world,
        Entity device,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        ForwardPbrPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(descriptor.Shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.VertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.FragmentEntryPoint);
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(
            Wgpu.CreateWgslShaderModule(deviceHandle, descriptor.Shader.Wgsl, descriptor.Shader.Id.Value));
        var bindGroupLayout = world.OwnWgpu(PbrObjectBindGroupLayout.Create(
            deviceHandle, instanceVisibility: WGPUShaderStage.Vertex | WGPUShaderStage.Fragment));
        var lightingBindGroupLayout = world.OwnWgpu(PbrLightingBindGroupLayout.Create(deviceHandle));
        var iblBindGroupLayout = world.OwnWgpu(PbrIblBindGroupLayout.Create(deviceHandle));
        var pipelineLayout = world.OwnWgpu(PbrObjectBindGroupLayout.CreatePipelineLayout(
            deviceHandle,
            bindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            lightingBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            iblBindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var renderPipeline = world.OwnWgpu(CreateRenderPipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>(),
            colorFormat,
            depthFormat,
            descriptor));
        return new ForwardPbrPipeline(renderPipeline, bindGroupLayout, lightingBindGroupLayout, iblBindGroupLayout);
    }

    public static RenderPipelineKey GetKey(
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        ForwardPbrPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new RenderPipelineKey(
            descriptor.Shader.Id,
            descriptor.Shader.Revision,
            descriptor.VertexEntryPoint,
            descriptor.FragmentEntryPoint,
            "Sia.Engine.Mesh.MeshVertex/v1",
            colorFormat.ToString(),
            depthFormat.ToString(),
            descriptor.State).Validate();
    }

    public static ForwardPbrPipeline GetOrCreate(
        PipelineCache<RenderPipelineKey, ForwardPbrPipeline> cache,
        World world,
        Entity device,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        ForwardPbrPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var key = GetKey(colorFormat, depthFormat, descriptor);
        return cache.GetOrCreate(
            key,
            _ => Create(world, device, colorFormat, depthFormat, descriptor));
    }

    private static WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        ForwardPbrPipelineDescriptor pipelineDescriptor)
    {
        var vertexEntryPoint = Encoding.UTF8.GetBytes(pipelineDescriptor.VertexEntryPoint);
        var fragmentEntryPoint = Encoding.UTF8.GetBytes(pipelineDescriptor.FragmentEntryPoint);
        fixed (byte* vertexEntry = vertexEntryPoint)
        fixed (byte* fragmentEntry = fragmentEntryPoint) {
            Span<WGPUVertexAttribute> attributes = stackalloc WGPUVertexAttribute[MeshVertexLayout.AttributeCount];
            MeshVertexLayout.Fill(attributes);

            fixed (WGPUVertexAttribute* attributesPtr = attributes) {
                var vertexBuffer = WGPUVertexBufferLayout.Default;
                vertexBuffer.StepMode = WGPUVertexStepMode.Vertex;
                vertexBuffer.ArrayStride = MeshVertex.Stride;
                vertexBuffer.AttributeCount = (nuint)attributes.Length;
                vertexBuffer.Attributes = attributesPtr;

                var colorTarget = WGPUColorTargetState.Default;
                colorTarget.Format = colorFormat;
                colorTarget.WriteMask = WGPUColorWriteMask.All;
                var blend = CreateBlendState(pipelineDescriptor.State.BlendMode);
                if (pipelineDescriptor.State.BlendMode != RenderBlendMode.Opaque) {
                    colorTarget.Blend = &blend;
                }

                var fragment = WGPUFragmentState.Default;
                fragment.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
                fragment.EntryPoint = new WGPUStringView {
                    Data = fragmentEntry,
                    Length = (nuint)fragmentEntryPoint.Length
                };
                fragment.TargetCount = 1;
                fragment.Targets = &colorTarget;

                var depthStencil = WGPUDepthStencilState.Default;
                depthStencil.Format = depthFormat;
                depthStencil.DepthWriteEnabled = pipelineDescriptor.State.DepthWriteEnabled
                    ? WGPUOptionalBool.True
                    : WGPUOptionalBool.False;
                depthStencil.DepthCompare = Lower(pipelineDescriptor.State.DepthCompare);

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
                descriptor.Primitive.Topology = Lower(pipelineDescriptor.State.Topology);
                descriptor.Primitive.FrontFace = Lower(pipelineDescriptor.State.FrontFace);
                descriptor.Primitive.CullMode = Lower(pipelineDescriptor.State.CullMode);
                descriptor.DepthStencil = &depthStencil;
                descriptor.Multisample = WGPUMultisampleState.Default;
                descriptor.Multisample.Count = pipelineDescriptor.State.SampleCount;
                descriptor.Fragment = &fragment;
                return Wgpu.CreateRenderPipeline(device, in descriptor);
            }
        }
    }

    private static WGPUBlendState CreateBlendState(RenderBlendMode mode)
    {
        var result = WGPUBlendState.Default;
        result.Color.Operation = WGPUBlendOperation.Add;
        result.Alpha.Operation = WGPUBlendOperation.Add;
        switch (mode) {
            case RenderBlendMode.Opaque:
                break;
            case RenderBlendMode.Alpha:
                result.Color.SrcFactor = WGPUBlendFactor.SrcAlpha;
                result.Color.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
                result.Alpha.SrcFactor = WGPUBlendFactor.One;
                result.Alpha.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
                break;
            case RenderBlendMode.Additive:
                result.Color.SrcFactor = WGPUBlendFactor.SrcAlpha;
                result.Color.DstFactor = WGPUBlendFactor.One;
                result.Alpha.SrcFactor = WGPUBlendFactor.One;
                result.Alpha.DstFactor = WGPUBlendFactor.One;
                break;
            case RenderBlendMode.PremultipliedAlpha:
                result.Color.SrcFactor = WGPUBlendFactor.One;
                result.Color.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
                result.Alpha.SrcFactor = WGPUBlendFactor.One;
                result.Alpha.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return result;
    }

    private static WGPUPrimitiveTopology Lower(RenderPrimitiveTopology topology) =>
        topology switch {
            RenderPrimitiveTopology.PointList => WGPUPrimitiveTopology.PointList,
            RenderPrimitiveTopology.LineList => WGPUPrimitiveTopology.LineList,
            RenderPrimitiveTopology.LineStrip => WGPUPrimitiveTopology.LineStrip,
            RenderPrimitiveTopology.TriangleList => WGPUPrimitiveTopology.TriangleList,
            RenderPrimitiveTopology.TriangleStrip => WGPUPrimitiveTopology.TriangleStrip,
            _ => throw new ArgumentOutOfRangeException(nameof(topology))
        };

    private static WGPUFrontFace Lower(RenderFrontFace frontFace) =>
        frontFace switch {
            RenderFrontFace.CounterClockwise => WGPUFrontFace.CCW,
            RenderFrontFace.Clockwise => WGPUFrontFace.CW,
            _ => throw new ArgumentOutOfRangeException(nameof(frontFace))
        };

    private static WGPUCullMode Lower(RenderCullMode cullMode) =>
        cullMode switch {
            RenderCullMode.None => WGPUCullMode.None,
            RenderCullMode.Front => WGPUCullMode.Front,
            RenderCullMode.Back => WGPUCullMode.Back,
            _ => throw new ArgumentOutOfRangeException(nameof(cullMode))
        };

    private static WGPUCompareFunction Lower(RenderCompareFunction compare) =>
        compare switch {
            RenderCompareFunction.Never => WGPUCompareFunction.Never,
            RenderCompareFunction.Less => WGPUCompareFunction.Less,
            RenderCompareFunction.Equal => WGPUCompareFunction.Equal,
            RenderCompareFunction.LessEqual => WGPUCompareFunction.LessEqual,
            RenderCompareFunction.Greater => WGPUCompareFunction.Greater,
            RenderCompareFunction.NotEqual => WGPUCompareFunction.NotEqual,
            RenderCompareFunction.GreaterEqual => WGPUCompareFunction.GreaterEqual,
            RenderCompareFunction.Always => WGPUCompareFunction.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(compare))
        };
}
