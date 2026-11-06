namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    // The complete required set has already passed aggregate admission. Only
    // this bounded window may acquire/decode concurrently; results retain input order.
    internal static async Task<TResult[]> ReadRequiredAsync<TInput, TResult>(IReadOnlyList<TInput> inputs,
        Func<TInput, CancellationToken, Task<TResult>> read, CancellationToken token, int concurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        token.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new List<Task<(int Index, TResult Result)>>(System.Math.Min(concurrency, inputs.Count));
        var results = new TResult[inputs.Count];
        var next = 0;
        var failureGate = new object(); Exception? failure = null;
        async Task<(int Index, TResult Result)> Fetch(int index)
        {
            try { return (index, await read(inputs[index], cancellation.Token)); }
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
                cancellation.Token.ThrowIfCancellationRequested();
                pending.Add(Fetch(next++));
            }
        }
        try {
            Fill();
            while (pending.Count > 0) {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var (index, result) = await completed;
                results[index] = result;
                cancellation.Token.ThrowIfCancellationRequested();
                Fill();
            }
            return results;
        }
        catch {
            // Keep the initiating error and join every prefetched reader before
            // its source/cache can be disposed. Results here own no GPU resources.
            try { await cancellation.CancelAsync(); }
            catch { /* A cancellation callback cannot replace the read failure. */ }
            foreach (var task in pending) {
                try { await task; }
                catch { /* Observe abandoned tasks, retaining the initiating error. */ }
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }
}
