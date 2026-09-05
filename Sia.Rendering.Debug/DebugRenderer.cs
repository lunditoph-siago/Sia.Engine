using Sia;
using Sia.Engine.Camera;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Debug;

public sealed class DebugRenderer
{
    private const ulong InitialVertexBufferCapacity = 64UL * 1024;

    private readonly DebugPipeline _pipeline;
    private World? _resourceWorld;
    private Entity _cameraBuffer;
    private Entity _vertexBuffer;
    private Entity _bindGroup;
    private ulong _vertexBufferCapacity;
    private int _preparedVertexCount;

    private DebugRenderer(DebugDrawList drawList, DebugPipeline pipeline)
    {
        DrawList = drawList;
        _pipeline = pipeline;
    }

    public DebugDrawList DrawList { get; }

    public static DebugRenderer Create(
        World resourceWorld,
        Entity device,
        WGPUTextureFormat colorFormat,
        WGPUTextureFormat depthFormat,
        DebugDrawList? drawList = null)
    {
        ArgumentNullException.ThrowIfNull(resourceWorld);
        var pipeline = DebugPipeline.Create(
            resourceWorld,
            device,
            colorFormat,
            depthFormat);
        return new DebugRenderer(drawList ?? new DebugDrawList(), pipeline);
    }

    public void Prepare(in GpuFrame frame, Entity camera)
    {
        _preparedVertexCount = DrawList.VertexCount;
        if (_preparedVertexCount == 0) {
            return;
        }

        BindResourceWorld(frame.ResourceWorld);
        EnsureCameraResources(in frame);
        EnsureVertexBuffer(in frame);

        var matrices = camera.Get<CameraMatrices>();
        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(),
            _cameraBuffer.GetWgpu<WGPUBuffer>(),
            0,
            [new DebugCameraUniformData(matrices.ViewProj)]);
        Wgpu.WriteBuffer(
            frame.Queue.GetWgpu<WGPUQueue>(),
            _vertexBuffer.GetWgpu<WGPUBuffer>(),
            0,
            DrawList.Vertices);
    }

    public void Encode(WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        if (_preparedVertexCount == 0) {
            return;
        }
        Wgpu.SetRenderPipeline(
            renderPass,
            _pipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(
            renderPass,
            0,
            _bindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetVertexBuffer(
            renderPass,
            0,
            _vertexBuffer.GetWgpu<WGPUBuffer>(),
            size: checked((ulong)_preparedVertexCount * DebugVertex.Stride));
        Wgpu.Draw(renderPass, (uint)_preparedVertexCount);
    }

    private void BindResourceWorld(World world)
    {
        if (_resourceWorld is null) {
            _resourceWorld = world;
            return;
        }
        if (!ReferenceEquals(_resourceWorld, world)) {
            throw new InvalidOperationException(
                "A debug renderer cannot be shared by multiple frame worlds.");
        }
    }

    private void EnsureCameraResources(in GpuFrame frame)
    {
        if (_cameraBuffer.IsValid) {
            return;
        }

        _cameraBuffer = frame.ResourceWorld.CreateWgpuBuffer(
            frame.Device,
            new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = DebugCameraUniformData.Stride,
                MappedAtCreation = 0,
            });
        _bindGroup = frame.ResourceWorld.OwnWgpu(_pipeline.CreateBindGroup(
            frame.Device.GetWgpu<WGPUDevice>(),
            _cameraBuffer.GetWgpu<WGPUBuffer>()));
    }

    private void EnsureVertexBuffer(in GpuFrame frame)
    {
        var requiredBytes = checked((ulong)DrawList.VertexCount * DebugVertex.Stride);
        if (requiredBytes <= _vertexBufferCapacity) {
            return;
        }

        var capacity = _vertexBufferCapacity == 0
            ? InitialVertexBufferCapacity
            : _vertexBufferCapacity;
        while (capacity < requiredBytes) {
            capacity = checked(capacity * 2);
        }

        if (_vertexBuffer.IsValid) {
            _vertexBuffer.Destroy();
        }
        _vertexBuffer = frame.ResourceWorld.CreateWgpuBuffer(
            frame.Device,
            new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst,
                Size = capacity,
                MappedAtCreation = 0,
            });
        _vertexBufferCapacity = capacity;
    }
}
