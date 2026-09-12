using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Sia.Engine.Example;

internal static class SceneDownload
{
    internal static async Task<ReadOnlyMemory<byte>> DownloadAsync(HttpClient client, string path,
        Action<string, double> report, CancellationToken cancellationToken = default, TimeSpan? idleTimeout = null)
    {
        const int maximumBytes = 512 * 1024 * 1024;
        using var bytes = new MemoryStream();
        var buffer = new byte[1024 * 1024];
        EntityTagHeaderValue? validator = null;
        long? length = null;
        var failures = 0;
        var timeout = idleTimeout ?? TimeSpan.FromMinutes(2);
        report("Loading scene", double.NaN);
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            if (failures > 0) report($"Reconnecting · {bytes.Length / 1048576.0:F1} MiB saved",
                length is > 0 ? (double)bytes.Length / length.Value : double.NaN);
            var retryDelay = TimeSpan.FromSeconds(System.Math.Min(30, 1 << System.Math.Min(failures, 5)));
            try {
                using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stalled.CancelAfter(timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (bytes.Length > 0 && validator is not null) {
                    request.Headers.Range = new RangeHeaderValue(bytes.Length, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(validator);
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && request.Headers.Range is not null) {
                    bytes.SetLength(0);
                    validator = null;
                    length = null;
                    continue;
                }
                if (response.Headers.RetryAfter is { } after) {
                    var wait = after.Delta ?? (after.Date - DateTimeOffset.UtcNow);
                    if (wait > retryDelay) retryDelay = wait.Value;
                }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException(
                    $"Unable to download scene (HTTP {(int)response.StatusCode}: {response.ReasonPhrase}).", null, response.StatusCode);
                var encoded = response.Content.Headers.ContentEncoding.Count != 0;
                long? responseEnd;
                if (response.StatusCode == HttpStatusCode.PartialContent) {
                    var range = response.Content.Headers.ContentRange;
                    if (request.Headers.Range is null || range?.Unit != "bytes" || range.From != bytes.Length
                        || range.To is null || range.Length is null || range.To < range.From || range.To >= range.Length) {
                        throw new InvalidDataException("The scene server returned an invalid download range.");
                    }
                    if (encoded || !Equals(response.Headers.ETag, validator) || (length.HasValue && range.Length != length)) {
                        // Do not append a changed representation to bytes already downloaded.
                        bytes.SetLength(0);
                        validator = null;
                        length = null;
                        continue;
                    }
                    length = range.Length;
                    responseEnd = range.To + 1;
                    if (response.Content.Headers.ContentLength is { } size && size != responseEnd - bytes.Length) {
                        throw new InvalidDataException("The scene server returned an inconsistent download range size.");
                    }
                } else {
                    // A server may ignore Range or return 200 when If-Range no longer matches.
                    bytes.SetLength(0);
                    length = encoded ? null : response.Content.Headers.ContentLength;
                    validator = !encoded && response.Headers.ETag is { IsWeak: false } etag ? etag : null;
                    responseEnd = length;
                }
                if (length > maximumBytes) throw new InvalidDataException("The scene download exceeds the supported size.");
                if (length > bytes.Capacity) bytes.Capacity = (int)length.Value;
                var stage = response.Headers.TryGetValues("X-Sia-Asset-Cache", out var cache) && cache.Contains("hit")
                    ? "Loading cached scene" : "Loading scene";
                var timer = Stopwatch.StartNew();
                var startedAt = bytes.Length;
                var lastReport = TimeSpan.Zero;
                using var stream = await response.Content.ReadAsStreamAsync(stalled.Token);
                while (true) {
                    // Activity extends the deadline; a slow transfer has no total time limit.
                    stalled.CancelAfter(timeout);
                    var count = await stream.ReadAsync(buffer, stalled.Token);
                    if (count == 0) break;
                    if (bytes.Length + count > maximumBytes || (responseEnd.HasValue && bytes.Length + count > responseEnd)) {
                        throw new InvalidDataException("The scene download exceeds the expected size.");
                    }
                    bytes.Write(buffer, 0, count);
                    failures = 0;
                    if (timer.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250)) {
                        lastReport = timer.Elapsed;
                        var speed = (bytes.Length - startedAt) / timer.Elapsed.TotalSeconds / 1024;
                        report($"{stage} · {bytes.Length / 1048576.0:F1} MiB · {speed:F0} KiB/s",
                            length is > 0 ? (double)bytes.Length / length.Value : double.NaN);
                    }
                }
                if (length.HasValue && bytes.Length != length) throw new EndOfStreamException("The scene download was interrupted.");
                report(stage, 1);
                return bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && (error is OperationCanceledException or IOException
                    || error is HttpRequestException http && (http.StatusCode is null
                        || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                        || (int)http.StatusCode >= 500))) {
                failures++;
                if (validator is null) bytes.SetLength(0);
                report($"Connection interrupted · {bytes.Length / 1048576.0:F1} MiB saved · retrying in {retryDelay.TotalSeconds:F0}s",
                    length is > 0 ? (double)bytes.Length / length.Value : double.NaN);
            }
            await Task.Delay(retryDelay, cancellationToken);
        }
    }
}
