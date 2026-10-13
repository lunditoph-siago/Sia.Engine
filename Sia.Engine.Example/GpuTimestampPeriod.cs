using System.Runtime.InteropServices;
using Sia.WebGPU;

namespace Sia.Engine.Example;

internal readonly struct GpuTimestampPeriod
{
    public float NanosecondsPerTick { get; }

    public GpuTimestampPeriod(float nanosecondsPerTick)
    {
        if (!float.IsFinite(nanosecondsPerTick) || nanosecondsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(nanosecondsPerTick));
        NanosecondsPerTick = nanosecondsPerTick;
    }

    public static GpuTimestampPeriod Read(WgpuHandle<WGPUQueue> queue)
    {
        var handle = queue.DangerousGetHandle();
        if (handle == 0) throw new ArgumentException("A live GPU queue is required.", nameof(queue));
#if BROWSER
        // WebGPU resolves timestamp queries to nanoseconds.
        return new(1);
#else
        return new(GetTimestampPeriod(handle));
#endif
    }

    public double GetMilliseconds(ulong begin, ulong end)
    {
        if (NanosecondsPerTick <= 0)
            throw new InvalidOperationException("The GPU timestamp period has not been initialized.");
        if (end < begin)
            throw new InvalidOperationException("GPU timestamps out of order.");
        // Subtract integers first: converting the absolute counters loses short spans above 2^53.
        return (end - begin) * (double)NanosecondsPerTick / 1_000_000;
    }

#if !BROWSER
    [DllImport("wgpu_native", EntryPoint = "wgpuQueueGetTimestampPeriod",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern float GetTimestampPeriod(nint queue);
#endif
}
