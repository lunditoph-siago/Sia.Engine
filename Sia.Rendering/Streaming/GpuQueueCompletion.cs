using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

/// <summary>Nonblocking completion for work already submitted to one WebGPU queue.</summary>
public static unsafe class GpuQueueCompletion
{
    public static Task<bool> AfterSubmittedWork(WgpuHandle<WGPUQueue> queue)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        try {
            var callback = new WGPUQueueWorkDoneCallbackInfo {
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus, WGPUStringView, void*, void*, void>)&Completed,
                Userdata1 = (void*)GCHandle.ToIntPtr(handle)
            };
            WgpuUnsafe.wgpuQueueOnSubmittedWorkDone((WGPUQueue*)queue.DangerousGetHandle(), callback);
        }
        catch { handle.Free(); throw; }
        return completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Completed(WGPUQueueWorkDoneStatus status, WGPUStringView message, void* user, void* unused)
    {
        var handle = GCHandle.FromIntPtr((nint)user);
        try {
            ((TaskCompletionSource<bool>)handle.Target!).TrySetResult(status == WGPUQueueWorkDoneStatus.Success);
        }
        finally { handle.Free(); }
    }
}
