using Sia.WebGPU;
using System.Runtime.InteropServices;

namespace Sia.Engine.Example;

internal readonly record struct GpuTimestampClock
{
    public double NanosecondsPerTick { get; }

    public GpuTimestampClock(double nanosecondsPerTick)
    {
        if (!double.IsFinite(nanosecondsPerTick) || nanosecondsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(nanosecondsPerTick));
        NanosecondsPerTick = nanosecondsPerTick;
    }

    public double Milliseconds(ulong first, ulong last)
    {
        if (NanosecondsPerTick <= 0) throw new InvalidOperationException("GPU timestamp clock is not initialized.");
        if (last < first) throw new InvalidOperationException("GPU timestamps out of order.");
        // Subtract integer counters before floating-point conversion so a small
        // interval remains accurate even after the absolute clock exceeds 2^53.
        return (last - first) * NanosecondsPerTick / 1_000_000d;
    }

    public static GpuTimestampClock ForQueue(WgpuHandle<WGPUQueue> queue)
    {
        if (queue.IsNull) throw new ArgumentException("GPU queue is required.", nameof(queue));
#if BROWSER
        return new(1); // Browser WebGPU timestamp queries are in nanoseconds.
#else
        return new(NativePeriod(queue.DangerousGetHandle()));
#endif
    }

#if !BROWSER
    [DllImport("wgpu_native", EntryPoint = "wgpuQueueGetTimestampPeriod", CallingConvention = CallingConvention.Cdecl)]
    private static extern float NativePeriod(nint queue);
#endif
}
