using System.Buffers;

namespace Sia.Engine.Example;

internal static class AssetInput
{
    internal static bool TryGetHttpUri(string path, out Uri? uri)
        => Uri.TryCreate(path, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

    // The caller owns the returned stream; the shared client remains alive for scene chunks.
    internal static async Task<Stream> OpenAsync(string path, HttpClient client,
        CancellationToken cancellationToken = default)
        => TryGetHttpUri(path, out var uri)
            ? await client.GetStreamAsync(uri, cancellationToken)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static async Task<byte[]> ReadExactAsync(Stream input, int size,
        CancellationToken cancellationToken = default)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(size);
        await input.ReadExactlyAsync(bytes, cancellationToken);
        var trailing = new byte[1];
        if (await input.ReadAsync(trailing, cancellationToken) != 0)
            throw new InvalidDataException("Trailing asset data.");
        return bytes;
    }

    internal static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream input, int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var capacity = 0;
        if (input.CanSeek) {
            var remaining = input.Length - input.Position;
            if (remaining < 0 || remaining > maximumBytes)
                throw new InvalidDataException("Asset payload exceeds its size limit.");
            capacity = checked((int)remaining);
        }

        using var output = new MemoryStream(capacity);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try {
            while (true) {
                // One extra byte detects oversized responses without growing the output.
                var requested = (int)System.Math.Min(buffer.Length, (long)maximumBytes - output.Length + 1);
                var count = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (count == 0)
                    return output.GetBuffer().AsMemory(0, checked((int)output.Length));
                if (output.Length + count > maximumBytes)
                    throw new InvalidDataException("Asset payload exceeds its size limit.");

                var required = checked((int)output.Length + count);
                if (required > output.Capacity)
                    output.Capacity = (int)System.Math.Min(maximumBytes,
                        System.Math.Max((long)required, (long)output.Capacity * 2));
                output.Write(buffer, 0, count);
            }
        }
        finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
