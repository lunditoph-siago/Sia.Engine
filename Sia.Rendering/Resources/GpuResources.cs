using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed class GpuResources : IDisposable
{
    private readonly World _world;
    private readonly List<Entity> _owned = [];
    private readonly Dictionary<Entity, ulong> _sizes = [];
    private HashSet<Entity>? _releasing;
    private bool _disposed;
    private bool _disposing;

    public WgpuHandle<WGPUDevice> Device { get; }
    public WgpuHandle<WGPUQueue> Queue { get; }
    public WGPULimits Limits { get; }
    public ulong Budget { get; }
    public ulong Bytes { get; private set; }

    public GpuResources(in GpuFrame frame, ulong budget)
    {
        _world = frame.ResourceWorld;
        Device = frame.Device.GetWgpu<WGPUDevice>();
        Queue = frame.Queue.GetWgpu<WGPUQueue>();
        Limits = Wgpu.GetLimits(Device);
        Budget = budget;
    }

    public Entity Own<T>(WgpuHandle<T> value, ulong bytes = 0) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes > Budget - Bytes) {
            Wgpu.Release(ref value);
            throw new InvalidOperationException($"GPU resource budget exceeded: {Bytes} + {bytes} > {Budget}.");
        }
        Entity entity = default;
        try {
            entity = _world.OwnWgpu(value);
            _owned.Add(entity);
            _sizes.Add(entity, bytes);
            Bytes += bytes;
            return entity;
        }
        catch {
            if (entity.IsValid) entity.Destroy(); else Wgpu.Release(ref value);
            throw;
        }
    }

    public Entity Buffer(ulong size, WGPUBufferUsage usage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        size = checked((System.Math.Max(16ul, size) + 3) & ~3ul);
        if (size > Budget - Bytes || size > Limits.MaxBufferSize
            || ((usage & WGPUBufferUsage.Storage) != 0 && size > Limits.MaxStorageBufferBindingSize)
            || ((usage & WGPUBufferUsage.Uniform) != 0 && size > Limits.MaxUniformBufferBindingSize))
            throw new ArgumentOutOfRangeException(nameof(size), $"Buffer requires {size} bytes; remaining owner budget {Budget - Bytes}, maximum buffer {Limits.MaxBufferSize}, storage binding {Limits.MaxStorageBufferBindingSize}, uniform binding {Limits.MaxUniformBufferBindingSize}.");
        var descriptor = WGPUBufferDescriptor.Default;
        descriptor.Size = size;
        descriptor.Usage = usage;
        var entity = Own(Wgpu.CreateBuffer(Device, descriptor), size);
        entity.AddMany(HList.From(new WgpuBufferInfo(size, usage)));
        return entity;
    }

    public Entity Upload<T>(ReadOnlySpan<T> data, WGPUBufferUsage usage = WGPUBufferUsage.Storage) where T : unmanaged
    {
        var buffer = Buffer(checked((ulong)data.Length * (ulong)Unsafe.SizeOf<T>()), usage | WGPUBufferUsage.CopyDst);
        if (!data.IsEmpty) Wgpu.WriteBuffer(Queue, buffer.GetWgpu<WGPUBuffer>(), 0, data);
        return buffer;
    }

    public Entity Texture(in WGPUTextureDescriptor descriptor, ulong bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var supported = descriptor.Dimension switch {
            WGPUTextureDimension._2D => descriptor.Size.Width <= Limits.MaxTextureDimension2D
                && descriptor.Size.Height <= Limits.MaxTextureDimension2D
                && descriptor.Size.DepthOrArrayLayers <= Limits.MaxTextureArrayLayers,
            WGPUTextureDimension._3D => descriptor.Size.Width <= Limits.MaxTextureDimension3D
                && descriptor.Size.Height <= Limits.MaxTextureDimension3D
                && descriptor.Size.DepthOrArrayLayers <= Limits.MaxTextureDimension3D,
            _ => false
        };
        if (!supported || descriptor.Size.Width == 0 || descriptor.Size.Height == 0 || descriptor.Size.DepthOrArrayLayers == 0)
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Texture dimensions exceed device limits; this owner supports 2D arrays and 3D textures.");
        if (bytes > Budget - Bytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Texture exceeds owner budget.");
        var texture = Own(Wgpu.CreateTexture(Device, descriptor), bytes);
        texture.AddMany(HList.From(new WgpuTextureInfo(descriptor.Size, descriptor.Dimension, descriptor.Format,
            descriptor.Usage, descriptor.MipLevelCount, descriptor.SampleCount)));
        return texture;
    }

    public void Release(Entity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sizes.ContainsKey(entity) || !(_releasing ??= []).Add(entity))
            throw new InvalidOperationException("Resource belongs to a different owner or was already released.");
        if (_world.Dispatcher.IsSending) DeferRelease(entity);
        else ReleaseReservedResource(entity);
    }

    private void DeferRelease(Entity entity)
    {
        try { _world.Dispatcher.RunAfterSend(() => ReleaseReservedResource(entity)); }
        catch {
            _releasing!.Remove(entity);
            throw;
        }
    }

    private void ReleaseReservedResource(Entity entity)
    {
        try { ReleaseResource(entity); }
        finally { _releasing!.Remove(entity); }
    }

    private void ReleaseResource(Entity entity)
    {
        if (!_sizes.ContainsKey(entity)) return;
        try {
            if (entity.IsValid) entity.Destroy();
        }
        finally {
            // Destruction can fail or be deferred. Retain ownership until the entity
            // is actually invalid, so a later Dispose can retry the live resource.
            if (!entity.IsValid && _sizes.Remove(entity, out var bytes)) {
                _owned.Remove(entity);
                Bytes -= bytes;
            }
        }
    }

    public void Dispose()
    {
        if (_disposing || (_disposed && _owned.Count == 0)) return;
        _disposed = true;
        _disposing = true;
        try {
            // Defer the whole cleanup during sends rather than clearing its ledger
            // while World destruction is still pending. Reentry shares this attempt.
            _world.Dispatcher.RunAfterSend(DisposeResources);
        }
        catch {
            _disposing = false;
            throw;
        }
    }

    private void DisposeResources()
    {
        List<Exception>? errors = null;
        try {
            for (var i = _owned.Count - 1; i >= 0; i--) {
                try { ReleaseResource(_owned[i]); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }
        finally {
            _disposing = false;
        }
        if (errors is { Count: 1 }) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
    }
}
