using System.Runtime.CompilerServices;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

/// <summary>A retained upload mirror. Buffer entities belong to the frame's resource world.</summary>
public sealed class RetainedGpuBuffer<T> where T : unmanaged, IEquatable<T>
{
    private T[] _accepted = [];
    private World? _world;
    private Entity _device, _queue;
    public Entity Buffer { get; private set; }
    public ulong Capacity { get; private set; }
    public bool IsValid => Buffer.IsValid;
    public ulong UploadedBytes { get; private set; }
    public int UploadCalls { get; private set; }

    public bool Upload(in GpuFrame frame, ReadOnlySpan<T> values)
    {
        if (_world is not null && (_world != frame.ResourceWorld || _device != frame.Device || _queue != frame.Queue))
            throw new InvalidOperationException("A retained buffer cannot change its resource world, device or queue.");
        var stride = (ulong)Unsafe.SizeOf<T>();
        if (stride % 4 != 0) throw new InvalidOperationException("WebGPU buffer uploads require a four-byte aligned element stride.");
        var required = checked((ulong)System.Math.Max(1, values.Length) * stride);
        var resized = !IsValid || required > Capacity;
        UploadedBytes = 0; UploadCalls = 0;
        if (resized) {
            var limits = Wgpu.GetLimits(frame.Device.GetWgpu<WGPUDevice>());
            var maximum = System.Math.Min(limits.MaxBufferSize, limits.MaxStorageBufferBindingSize);
            if (required > maximum) throw new ArgumentOutOfRangeException(nameof(values), "Storage buffer exceeds device limits.");
            var capacity = System.Math.Min(maximum, System.Math.Max(required, Capacity > maximum / 2 ? maximum : Capacity * 2));
            var next = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                Size = capacity, Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc
            });
            try {
                Wgpu.WriteBuffer<T>(frame.Queue.GetWgpu<WGPUQueue>(), next.GetWgpu<WGPUBuffer>(), 0, values);
            }
            catch { next.Destroy(); throw; }
            var previous = Buffer;
            Buffer = next; Capacity = capacity;
            _world = frame.ResourceWorld; _device = frame.Device; _queue = frame.Queue;
            if (previous.IsValid) previous.Destroy();
            UploadedBytes = (ulong)values.Length * stride; UploadCalls = values.IsEmpty ? 0 : 1;
        } else {
            for (var i = 0; i < values.Length;) {
                if (i < _accepted.Length && values[i].Equals(_accepted[i])) { i++; continue; }
                var start = i++;
                while (i < values.Length && (i >= _accepted.Length || !values[i].Equals(_accepted[i]))) i++;
                Wgpu.WriteBuffer<T>(frame.Queue.GetWgpu<WGPUQueue>(), Buffer.GetWgpu<WGPUBuffer>(), (ulong)start * stride, values[start..i]);
                UploadedBytes += (ulong)(i - start) * stride; UploadCalls++;
            }
        }
        if (resized || UploadedBytes != 0 || _accepted.Length != values.Length) _accepted = values.ToArray();
        return resized;
    }
}
