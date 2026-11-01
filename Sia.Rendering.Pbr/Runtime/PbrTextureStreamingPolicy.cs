namespace Sia.Engine.Rendering.Pbr;

internal static class PbrTextureStreamingPolicy
{
    internal static ulong AvailableBytes(ulong budget, ulong used, ulong reserved = 0)
        => used >= budget || reserved >= budget - used ? 0 : budget - used - reserved;

    internal static int MinimumMip(uint width, uint height, int tailMip, uint maximumDimension,
        long decodedBytes, TextureBlockLayout layout)
    {
        for (var mip = 0; mip <= tailMip; mip++) {
            var w = System.Math.Max(1u, width >> mip); var h = System.Math.Max(1u, height >> mip);
            if (w <= maximumDimension && h <= maximumDimension && layout.SupportsBaseDimensions(w, h)
                && (mip == tailMip || layout.MipByteLength(w, h) <= (ulong)System.Math.Max(0, decodedBytes))) return mip;
        }
        throw new NotSupportedException("No available bootstrap texture mip fits the device dimensions and storage blocks.");
    }

    internal static bool IsObsolete(ReadOnlySpan<int> resident, ReadOnlySpan<int> desired, ReadOnlySpan<int> requested)
    {
        if (resident.Length != desired.Length || resident.Length != requested.Length)
            throw new ArgumentException("Texture layer states differ.");
        for (var layer = 0; layer < resident.Length; layer++) {
            if ((requested[layer] < resident[layer] && desired[layer] > requested[layer])
                || (requested[layer] > resident[layer] && desired[layer] < requested[layer])) return true;
        }
        return false;
    }

    internal static void ValidateBootstrap(ulong uploadBytes, ulong maximumBytes)
    {
        if (uploadBytes > maximumBytes)
            throw new InvalidOperationException($"Initial texture upload requires {uploadBytes} bytes; bootstrap limit is {maximumBytes} bytes.");
    }
}
