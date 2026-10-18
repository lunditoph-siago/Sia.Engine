namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrLightmapMetadata
{
    internal const int RootReceiverTexels = 7;

    internal static ulong RequiredBytes(PbrLightmapSetData set, int instances)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (instances is < 1 or > MaximumInstances) throw new ArgumentOutOfRangeException(nameof(instances));
        var texels = checked(2 + (instances + 3) / 4 + set.Receivers.Length * RootReceiverTexels);
        var width = System.Math.Min(256, texels);
        return checked((ulong)width * (ulong)((texels + width - 1) / width) * 16);
    }

    // Persistent root support costs only draw/binding rows. Product pointers start
    // at zero; independently resident member tables occupy a separate bounded slab.
    internal PbrLightmapMetadata(PbrLightmapSetData set, ReadOnlySpan<int> rows, ulong maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var bytes = RequiredBytes(set, rows.Length);
        if (bytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var row in rows)
            if (row < -1 || row >= set.Receivers.Length) throw new ArgumentException("Invalid root receiver row.", nameof(rows));
        var texels = 2 + (rows.Length + 3) / 4 + set.Receivers.Length * RootReceiverTexels;
        Width = System.Math.Min(256, texels); Height = (texels + Width - 1) / Width;
        Words = new uint[Width * Height * 4];
        var receiverStart = 2 + (rows.Length + 3) / 4;
        Write(0, (uint)rows.Length, (uint)set.Receivers.Length, (uint)set.Products.Length, 0);
        Write(1, 2, (uint)receiverStart, RootReceiverTexels, 0);
        for (var at = 0; at < rows.Length; at++) {
            if ((at & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            Words[8 + at] = (uint)(rows[at] + 1);
        }
        for (var at = 0; at < set.Receivers.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested(); var binding = set.Receivers[at]; var receiver = binding.Receiver;
            var first = receiverStart + at * RootReceiverTexels;
            Write(first, (uint)receiver.Resolution, 0, (uint)binding.ProductReceiver, (uint)binding.Product);
            Float(first + 1, receiver.Quantization.Constant); Float(first + 2, receiver.Quantization.Y);
            Float(first + 3, receiver.Quantization.Z); Float(first + 4, receiver.Quantization.X);
            receiver.Quantization.CopyPackedCoefficients(binding.Coarse, Words.AsSpan((first + 5) * 4, 8), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
