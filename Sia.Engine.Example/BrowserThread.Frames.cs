#if BROWSER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.Engine.Example;

internal sealed partial class BrowserThread
{
    // One graphics operation owns the whole loop. Returning false or throwing
    // stops native RAF before releasing its handle and allowing disposal.
    private sealed class FrameLoop(Func<double, bool> frame)
    {
        private readonly Func<double, bool> _frame = frame;
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SynchronizationContext _context = SynchronizationContext.Current!;

        public static unsafe Task<int> RunAsync(Func<double, bool> frame)
        {
            GraphicsContext.VerifyAccess();
            ArgumentNullException.ThrowIfNull(frame);
            var loop = new FrameLoop(frame);
            var handle = GCHandle.Alloc(loop);
            try { RequestLoop(&Tick, GCHandle.ToIntPtr(handle)); }
            catch { handle.Free(); throw; }
            return loop._completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Tick(double timestamp, nint state)
        {
            var handle = GCHandle.FromIntPtr(state);
            var loop = (FrameLoop)handle.Target!;
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(loop._context);
            try {
                GraphicsContext.VerifyAccess();
                if (loop._frame(timestamp)) return 1;
                handle.Free();
                loop._completion.TrySetResult(0);
            }
            catch (Exception error) {
                handle.Free();
                loop._completion.TrySetException(error);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return 0;
        }

        [DllImport("__Internal_emscripten", EntryPoint = "emscripten_request_animation_frame_loop", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe void RequestLoop(delegate* unmanaged[Cdecl]<double, nint, int> callback, nint state);
    }
}
#endif
