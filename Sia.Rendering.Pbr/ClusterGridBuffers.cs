using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;
using Sia.Engine.Camera;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ClusterConfigGpu(
    float4x4 View, float4x4 InvProj, uint4 Tiles, float4 ZFactors, float4 Screen)
{
    public const int Stride = 176;
}

public sealed class ClusterGridBuffers
{
    private Entity _configBuffer;
    private Entity _lightGridBuffer;
    private Entity _lightIndexListBuffer;
    private uint _configuredClusterCount;
    private uint _configuredMaxIndices;

    public Entity ConfigBuffer => _configBuffer;

    public Entity LightGridBuffer => _lightGridBuffer;

    public ulong LightGridSize { get; private set; }

    public Entity LightIndexListBuffer => _lightIndexListBuffer;

    public ulong LightIndexListCapacity { get; private set; }

    public bool IsValid => _configBuffer.IsValid;

    public bool EnsureCapacity(in GpuFrame frame, ClusterGridConfig config)
    {
        var clusterCount = config.ClusterCount;
        if (_configBuffer.IsValid
            && clusterCount == _configuredClusterCount
            && config.MaxLightIndicesPerCluster == _configuredMaxIndices) {
            return false;
        }

        DestroyBuffers();

        _configBuffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = ClusterConfigGpu.Stride,
            MappedAtCreation = 0
        });

        LightGridSize = System.Math.Max((ulong)clusterCount * 8, 8);
        _lightGridBuffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = LightGridSize,
            MappedAtCreation = 0
        });

        LightIndexListCapacity = System.Math.Max(
            (ulong)clusterCount * config.MaxLightIndicesPerCluster * sizeof(uint), sizeof(uint));
        _lightIndexListBuffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
            Size = LightIndexListCapacity,
            MappedAtCreation = 0
        });

        _configuredClusterCount = clusterCount;
        _configuredMaxIndices = config.MaxLightIndicesPerCluster;
        return true;
    }

    public void UpdateConfig(
        in GpuFrame frame,
        ClusterGridConfig config,
        in CameraMatrices camera,
        float near,
        float far,
        int lightCount,
        uint framebufferWidth,
        uint framebufferHeight)
    {
        var factorA = config.ZSlices / MathF.Log(far / near);
        var factorB = MathF.Log(near) * factorA;

        var data = new ClusterConfigGpu(
            camera.View,
            math.inverse(camera.Proj),
            new uint4(config.TilesX, config.TilesY, config.ZSlices, config.MaxLightIndicesPerCluster),
            new float4(factorA, factorB, lightCount, 0.0f),
            new float4(framebufferWidth, framebufferHeight, 0.0f, 0.0f));
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(), _configBuffer.GetWgpu<WGPUBuffer>(), 0, [data]);
    }

    private void DestroyBuffers()
    {
        if (_configBuffer.IsValid) {
            _configBuffer.Destroy();
        }
        if (_lightGridBuffer.IsValid) {
            _lightGridBuffer.Destroy();
        }
        if (_lightIndexListBuffer.IsValid) {
            _lightIndexListBuffer.Destroy();
        }
    }
}
