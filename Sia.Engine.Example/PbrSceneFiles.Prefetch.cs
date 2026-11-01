namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    // The complete required set has already passed aggregate admission. Only
    // this bounded window may acquire/decode concurrently; publication is ordered.
    internal static async Task<TResult[]> ReadRequiredAsync<TInput, TResult>(IReadOnlyList<TInput> inputs,
        Func<TInput, CancellationToken, Task<TResult>> read, CancellationToken token, int concurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        token.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new Queue<Task<TResult>>(System.Math.Min(concurrency, inputs.Count));
        var results = new TResult[inputs.Count];
        var next = 0;
        var failureGate = new object(); Exception? failure = null;
        async Task<TResult> Fetch(TInput input)
        {
            try { return await read(input, cancellation.Token); }
            catch (Exception error) {
                lock (failureGate) {
                    if (failure is null && !(error is OperationCanceledException && cancellation.IsCancellationRequested))
                        failure = error;
                }
                try { await cancellation.CancelAsync(); }
                catch { /* Preserve the initiating read error. */ }
                throw;
            }
        }
        void Fill()
        {
            while (pending.Count < concurrency && next < inputs.Count) {
                var input = inputs[next++];
                pending.Enqueue(Fetch(input));
            }
        }
        try {
            Fill();
            for (var index = 0; index < results.Length; index++) {
                results[index] = await pending.Dequeue();
                token.ThrowIfCancellationRequested();
                Fill();
            }
            return results;
        }
        catch {
            // Keep the initiating error and join every prefetched reader before
            // its source/cache can be disposed. Results here own no GPU resources.
            try { await cancellation.CancelAsync(); }
            catch { /* A cancellation callback cannot replace the read failure. */ }
            while (pending.TryDequeue(out var task)) {
                try { await task; }
                catch { /* Observe abandoned tasks, retaining the initiating error. */ }
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }
}
