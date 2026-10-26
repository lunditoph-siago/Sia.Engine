using System.Collections.Immutable;

namespace Sia.Asset;

// One current block and one cancellable lookahead. No record-sized encoded concatenation.
internal sealed class ChunkReadStream(ImmutableArray<AssetChunk> chunks, AssetChunkCache cache,
    int priority, CancellationToken token) : Stream
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    private Task<AssetChunkLease>? _next;
    private AssetChunkLease? _lease;
    private byte[]? _decoded;
    private ReadOnlyMemory<byte> _bytes;
    private int _index, _offset;
    private bool _disposed;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        if (_offset == _bytes.Length) {
            _lease?.Dispose(); _lease = null; _bytes = default; _offset = 0;
            if (_index == chunks.Length) return 0;
            _next ??= cache.AcquireAsync(chunks[_index], priority, _stop.Token).AsTask();
            var lease = await _next.WaitAsync(cancellationToken).ConfigureAwait(false);
            _next = null; _lease = lease;
            var chunk = chunks[_index++];
            if (_index < chunks.Length) _next = cache.AcquireAsync(chunks[_index], priority, _stop.Token).AsTask();
            if (chunk.Encoding == AssetChunkEncoding.Raw) _bytes = lease.Memory;
            else {
                if (_decoded is null || _decoded.Length < chunk.DecodedLength)
                    _decoded = GC.AllocateUninitializedArray<byte>(chunk.DecodedLength);
                chunk.Decode(lease.Memory.Span, _decoded.AsSpan(0, chunk.DecodedLength));
                _bytes = _decoded.AsMemory(0, chunk.DecodedLength);
                _lease.Dispose(); _lease = null;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        var count = Math.Min(buffer.Length, _bytes.Length - _offset);
        _bytes.Span.Slice(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        _lease?.Dispose(); _lease = null; _bytes = default; _decoded = null;
        try { if (_next is not null) (await _next.ConfigureAwait(false)).Dispose(); }
        catch (Exception) { _ = _next?.Exception; }
        finally { _next = null; _stop.Dispose(); }
        GC.SuppressFinalize(this);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous typed decoding.");
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
