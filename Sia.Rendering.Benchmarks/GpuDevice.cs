using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed class GpuDevice : IDisposable
{
    public WgpuHandle<WGPUInstance> Instance;
    public WgpuHandle<WGPUAdapter> Adapter;
    public WgpuHandle<WGPUDevice> Device;
    public WgpuHandle<WGPUQueue> Queue;
    public bool TimingEnabled { get; private set; }
    public WGPULimits Limits { get; private set; }
    public AdapterDescription Description { get; private set; } = null!;
    private readonly List<string> _errors = [];
    private GCHandle _errorHandle;

    private GpuDevice() { }

    public static async Task<GpuDevice> CreateAsync(bool timing)
    {
        var gpu = new GpuDevice();
        try {
            gpu.Instance = Wgpu.CreateInstance();
            gpu.Adapter = await Wgpu.RequestAdapterAsync(gpu.Instance, new WGPURequestAdapterOptions {
                FeatureLevel = WGPUFeatureLevel.Core, PowerPreference = WGPUPowerPreference.HighPerformance
            });
            gpu.Device = await gpu.RequestDeviceAsync(timing);
            gpu.Queue = Wgpu.GetQueue(gpu.Device);
            gpu.Limits = Wgpu.GetLimits(gpu.Device);
            return gpu;
        }
        catch (Exception error) {
            try { gpu.Dispose(); }
            catch (Exception cleanupError) { error.Data["CleanupFailure"] = cleanupError; }
            throw;
        }
    }

    private unsafe Task<WgpuHandle<WGPUDevice>> RequestDeviceAsync(bool timing)
    {
        var info = WGPUAdapterInfo.Default;
        if (WgpuUnsafe.wgpuAdapterGetInfo((WGPUAdapter*)Adapter.DangerousGetHandle(), &info) != WGPUStatus.Success) {
            throw new InvalidOperationException("Cannot read adapter information.");
        }
        try { Description = new(Text(info.Vendor), Text(info.Device), Text(info.Description), info.BackendType.ToString()); }
        finally { WgpuUnsafe.wgpuAdapterInfoFreeMembers(info); }
        TimingEnabled = timing && WgpuUnsafe.wgpuAdapterHasFeature((WGPUAdapter*)Adapter.DangerousGetHandle(), WGPUFeatureName.TimestampQuery) != 0;
        _errorHandle = GCHandle.Alloc(_errors);
        var descriptor = WGPUDeviceDescriptor.Default;
        descriptor.UncapturedErrorCallbackInfo.Callback = &OnError;
        descriptor.UncapturedErrorCallbackInfo.Userdata1 = (void*)GCHandle.ToIntPtr(_errorHandle);
        var feature = WGPUFeatureName.TimestampQuery;
        descriptor.RequiredFeatureCount = TimingEnabled ? 1u : 0u;
        descriptor.RequiredFeatures = TimingEnabled ? &feature : null;
        return Wgpu.RequestDeviceAsync(Adapter, descriptor);
    }

    private static unsafe string Text(WGPUStringView value) => Marshal.PtrToStringUTF8((nint)value.Data, checked((int)value.Length)) ?? "";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnError(WGPUDevice** device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        var errors = (List<string>)GCHandle.FromIntPtr((nint)userdata1).Target!;
        lock (errors) { errors.Add(Text(message)); }
    }

    public void CheckErrors()
    {
        Wgpu.ProcessEvents(Instance);
        lock (_errors) { if (_errors.Count != 0) { throw new InvalidOperationException(string.Join(Environment.NewLine, _errors)); } }
    }

    public void Dispose() => DisposeAll(
        () => Wgpu.Release(ref Queue),
        () => { if (!Device.IsNull) { Wgpu.DestroyDevice(Device); } },
        () => Wgpu.Release(ref Device), () => Wgpu.Release(ref Adapter), () => Wgpu.Release(ref Instance),
        () => { if (_errorHandle.IsAllocated) { _errorHandle.Free(); } });

    internal static void DisposeAll(params Action[] actions)
    {
        List<Exception>? errors = null;
        foreach (var action in actions) {
            try { action(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) { throw new AggregateException(errors); }
    }
}

internal sealed record AdapterDescription(string Vendor, string Device, string Description, string Backend);
