namespace Sia.Asset;

public static class AssetChunkSources
{
    public static Func<AssetChunk, CancellationToken, ValueTask<Stream>> Directory(string directory)
    {
        var root = Path.GetFullPath(directory);
        return (chunk, cancellationToken) => {
            cancellationToken.ThrowIfCancellationRequested();
            // FileName contains only a validated content hash and a fixed suffix.
            Stream stream = new FileStream(Path.Combine(root, chunk.FileName), FileMode.Open, FileAccess.Read,
                FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(stream);
        };
    }

    public static Func<AssetChunk, CancellationToken, ValueTask<Stream>> Http(HttpClient client, Uri directory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(directory);
        if (!directory.IsAbsoluteUri || directory.Scheme is not ("http" or "https")
            || !directory.AbsolutePath.EndsWith('/') || directory.Query.Length != 0 || directory.Fragment.Length != 0) {
            throw new ArgumentException("Expected an absolute HTTP directory URI ending in '/'.", nameof(directory));
        }
        return async (chunk, cancellationToken) => {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(directory, chunk.FileName));
            // Modern browser HttpClient streams responses; do not request ResponseContentRead.
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Count == 0
                    && response.Content.Headers.ContentLength is { } length && length != chunk.Length) {
                    throw new InvalidDataException("HTTP chunk length does not match the manifest.");
                }
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new ResponseStream(stream, response);
            }
            catch { response.Dispose(); throw; }
        };
    }

    private sealed class ResponseStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => stream.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                try { stream.Dispose(); }
                finally { response.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
