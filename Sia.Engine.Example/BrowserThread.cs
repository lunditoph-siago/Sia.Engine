#if BROWSER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.Engine.Example;

// Managed imports run on the deputy; native GLFW/WebGPU runs on the browser UI.
// Exchange snapshots across this boundary, never synchronously wait or call
// a JavaScript import from a graphics operation. CPU jobs use neither owner.
internal sealed partial class BrowserThread
{
    private readonly int _managed = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext _imports = SynchronizationContext.Current!;
    private readonly GraphicsContext _graphics = new();
    private BrowserThread() { }
    public static async Task<BrowserThread> StartAsync()
    {
        // Initialize runtime timers on a preloaded worker before the first HTTP call.
        await Task.Run(() => Task.Delay(1));
        return new BrowserThread();
    }
    public void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _managed) throw new InvalidOperationException("Browser imports require the managed main thread.");
    }
    public void VerifyGraphicsAccess() => GraphicsContext.VerifyAccess();
    public async Task<T> RunCpuAsync<T>(Func<CancellationToken, T> compute, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        try { return await Task.Run(() => compute(cancellationToken), cancellationToken); }
        finally { VerifyAccess(); }
    }
    public async Task<T> RunGraphicsAsync<T>(Func<Task<T>> action)
    {
        VerifyAccess();
        try { return await _graphics.RunAsync(action); }
        finally { VerifyAccess(); }
    }

    public Task RunFramesAsync(Func<double, bool> frame) => RunGraphicsAsync(() => FrameLoop.RunAsync(frame));

    public Task RunImportsAsync(Action action)
    {
        VerifyGraphicsAccess();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _imports.Post(_ => {
            try { VerifyAccess(); action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }, null);
        return completion.Task;
    }

    private sealed class GraphicsContext : SynchronizationContext
    {
        // One queue for the page lifetime; callbacks may still be unwinding when
        // a completion reaches the deputy, so it must not destroy the queue.
        private readonly nint _queue = CreateQueue();
        private readonly nint _thread = MainThread();
        private Action<Exception>? _failure;

        public static void VerifyAccess()
        {
            if (IsBrowserThread() == 0) throw new InvalidOperationException("Browser I/O and rendering require the browser UI thread.");
        }

        public async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            if (_failure is not null) throw new InvalidOperationException("Await the previous graphics operation.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _failure = error => completion.TrySetException(error);
            try {
                Post(async _ => {
                    try { VerifyAccess(); completion.TrySetResult(await action()); }
                    catch (Exception error) { completion.TrySetException(error); }
                }, null);
                return await completion.Task;
            }
            finally { _failure = null; }
        }

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new NotSupportedException("Use asynchronous browser dispatch.");

        public override unsafe void Post(SendOrPostCallback callback, object? state)
        {
            var handle = GCHandle.Alloc(new Pending(this, callback, state, _failure
                ?? throw new InvalidOperationException("No active graphics operation.")));
            if (Enqueue(_queue, _thread, &Invoke, GCHandle.ToIntPtr(handle)) == 0) {
                handle.Free();
                throw new InvalidOperationException("Browser dispatch failed.");
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Invoke(nint pointer)
        {
            var handle = GCHandle.FromIntPtr(pointer);
            var pending = (Pending)handle.Target!;
            handle.Free();
            var previous = Current;
            SetSynchronizationContext(pending.Owner);
            try { pending.Callback(pending.State); }
            catch (Exception error) { pending.Failure(error); }
            finally { SetSynchronizationContext(previous); }
        }

        private sealed record Pending(GraphicsContext Owner, SendOrPostCallback Callback, object? State, Action<Exception> Failure);

        [DllImport("__Internal_emscripten", EntryPoint = "em_proxying_queue_create", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CreateQueue();
        [DllImport("__Internal_emscripten", EntryPoint = "emscripten_main_runtime_thread_id", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint MainThread();
        [DllImport("__Internal_emscripten", EntryPoint = "emscripten_is_main_browser_thread", CallingConvention = CallingConvention.Cdecl)]
        private static extern int IsBrowserThread();
        [DllImport("__Internal_emscripten", EntryPoint = "emscripten_proxy_async", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int Enqueue(nint queue, nint target, delegate* unmanaged[Cdecl]<nint, void> callback, nint state);
    }
}

#endif
