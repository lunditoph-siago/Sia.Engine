namespace Sia.Engine.Rendering.Pbr;

internal static class PbrTextureStreamingPolicy
{
    internal static int ArrayLayerLimit(long decodedBytes, long fullLayerBytes, uint maximumLayers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(fullLayerBytes, 1);
        // CPU admission may hold one 64 MiB raw mip without enlarging shared
        // GPU arrays. Preserve the former 32 MiB grouping and lower custom caps.
        var groupingBytes = System.Math.Min(decodedBytes, 32L * 1024 * 1024);
        return (int)System.Math.Min(maximumLayers, System.Math.Max(1L, groupingBytes / fullLayerBytes));
    }

    internal static uint UploadAllowance(uint maximumPerFrame, uint bytesPerSecond, double elapsedSeconds, ref double credit)
    {
        if (bytesPerSecond == 0) return maximumPerFrame;
        var elapsed = double.IsFinite(elapsedSeconds) ? System.Math.Clamp(elapsedSeconds, 0, 0.1) : 0;
        credit = System.Math.Min(maximumPerFrame, System.Math.Max(0, credit) + elapsed * bytesPerSecond);
        return (uint)credit & ~3u;
    }

    internal static bool StableObsolete(bool obsolete, int minimumFrames, ref int frames)
    {
        if (!obsolete) { frames = 0; return false; }
        frames = System.Math.Min(frames, minimumFrames - 1) + 1;
        return frames >= minimumFrames;
    }

    internal static ulong AvailableBytes(ulong budget, ulong used, ulong reserved = 0)
        => used >= budget || reserved >= budget - used ? 0 : budget - used - reserved;

    internal static (int Mip, long Bytes) UpgradeRange(uint width, uint height, int levels,
        int residentMip, int desiredMip, int arrayMip, int layers, long decodedAvailable,
        ulong allocationAvailable, TextureBlockLayout layout, long readBatchBytes)
    {
        var target = residentMip;
        long bytes = 0;
        var maximumBytes = System.Math.Min(decodedAvailable, readBatchBytes);
        for (var mip = residentMip - 1; mip >= desiredMip; mip--) {
            var nextBytes = checked((long)layout.MipByteLength(System.Math.Max(1u, width >> mip), System.Math.Max(1u, height >> mip)));
            if (nextBytes > maximumBytes - bytes) break;
            if (mip < arrayMip) {
                // CPU reads contain only this layer's missing interval; a finer
                // GPU base replaces the complete tail of every array layer.
                ulong allocation = 0;
                for (var level = mip; level < levels; level++)
                    allocation = checked(allocation + layout.MipByteLength(System.Math.Max(1u, width >> level), System.Math.Max(1u, height >> level)));
                if (checked(allocation * (ulong)layers) > allocationAvailable) break;
            }
            target = mip;
            bytes = checked(bytes + nextBytes);
        }
        return (target, bytes);
    }

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
