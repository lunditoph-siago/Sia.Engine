using System.Threading;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private sealed record DeviceLoss(long SessionGeneration, string Reason, string Message, DateTimeOffset Utc, long StopwatchTimestamp);

    // Callback userdata is a numeric session token, never a GCHandle or object address.
    // A callback that already captured an old session can only mutate that old session.
    private sealed class GpuErrorSession(nint generation)
    {
        internal readonly nint Generation = generation;
        private string? _error;
        private DeviceLoss? _loss;
        private int _expectedDestroy;
        internal string? Error => Volatile.Read(ref _error);
        internal DeviceLoss? Loss => Volatile.Read(ref _loss);
        internal bool TryRecordError(string error) => Interlocked.CompareExchange(ref _error, error, null) is null;
        internal void ExpectDestroy() => Volatile.Write(ref _expectedDestroy, 1);
        internal DeviceLoss? TryRecordLoss(string reason, bool destroyed, string message, DateTimeOffset utc, long timestamp)
        {
            if (destroyed && Volatile.Read(ref _expectedDestroy) != 0) return null;
            var loss = new DeviceLoss((long)Generation, reason, message, utc, timestamp);
            return Interlocked.CompareExchange(ref _loss, loss, null) is null ? loss : null;
        }
    }

    private static long _nextGpuSession;
    private static GpuErrorSession? _gpuCurrentSession;
    private readonly GpuErrorSession _gpuSession = BeginGpuErrorSession();
    internal static object? GpuDeviceLossSnapshot => Volatile.Read(ref _gpuCurrentSession)?.Loss;

    private static GpuErrorSession BeginGpuErrorSession()
    {
        var sequence = Interlocked.Increment(ref _nextGpuSession);
        if (sequence <= 0) throw new InvalidOperationException("GPU session generation exhausted.");
        // Checked pointer-sized conversion prevents aliasing on browser/WASM32 too.
        var session = new GpuErrorSession(checked((nint)sequence));
        Volatile.Write(ref _gpuCurrentSession, session);
        return session;
    }

    private static GpuErrorSession? FindGpuErrorSession(nint generation)
    {
        var session = Volatile.Read(ref _gpuCurrentSession);
        return session is not null && session.Generation == generation ? session : null;
    }
}
