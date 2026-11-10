namespace Sia.Engine.Rendering.Pbr;

internal static class PbrTextureStreamingPolicy
{
    internal readonly record struct BudgetInput(ulong[] TailBytes, int[] Wanted, int[] Admitted, int[] Priorities,
        int[]? MainWanted = null);

    // Coverage detail controls silhouettes as well as color. Keep its paired
    // normal independently refinable too, while retaining one shared source for
    // any opaque users. Whole/non-refinable sources need no storage partition.
    internal static HashSet<PbrTextureData> IndependentCoverageSources(ReadOnlySpan<PbrMaterialAsset> materials,
        IReadOnlyDictionary<PbrTextureData, PbrSceneStream.TextureInfo>? sources)
    {
        var result = new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance);
        if (sources is null) return result;
        void Add(PbrTextureData? texture)
        {
            if (texture is not null && sources.TryGetValue(texture, out var info) && info.TailMip > 0) result.Add(texture);
        }
        foreach (var material in materials) {
            if (!material.AlphaBlend) continue;
            Add(material.BaseColor); Add(material.Normal);
        }
        return result;
    }

    // Partition each original chunk in place conceptually: removing coverage
    // sources must not pull unrelated opaque neighbors across chunk boundaries.
    internal static IEnumerable<PbrTextureData[]> PartitionCoverageChunk(PbrTextureData[] chunk,
        IReadOnlySet<PbrTextureData> independent)
    {
        ArgumentNullException.ThrowIfNull(chunk); ArgumentNullException.ThrowIfNull(independent);
        if (!chunk.Any(independent.Contains)) { if (chunk.Length != 0) yield return chunk; yield break; }
        var ordinary = chunk.Where(texture => !independent.Contains(texture)).ToArray();
        if (ordinary.Length != 0) yield return ordinary;
        foreach (var texture in chunk)
            if (independent.Contains(texture)) yield return [texture];
    }

    internal static int RefinementTarget(int desired, int mainWanted, int resident)
        => mainWanted < resident ? System.Math.Max(desired, mainWanted) : desired;

    internal static int PriorityBand(float pixels, int previous)
    {
        if (pixels <= 0 || float.IsNaN(pixels)) return 0;
        // Half-octave bands distinguish visibility from coarse visible demand.
        // An eighth-octave guard on either edge prevents camera jitter from
        // repeatedly replacing an otherwise unchanged budget plan.
        var scaled = System.Math.Clamp(2 * System.Math.Log2(System.Math.Max(1, pixels)), 0, 62);
        if (previous > 0 && scaled >= previous - 1.25 && scaled < previous + 0.25) return previous;
        return (int)scaled + 1;
    }

    internal static int DemandMip(int wanted, int resident, int minimum, int idleFrames, ref int idle)
    {
        idle = wanted > resident ? System.Math.Min(idleFrames, idle + 1) : 0;
        return System.Math.Max(minimum, wanted <= resident || idle >= idleFrames ? wanted : resident);
    }

    internal static int[][] BudgetTargets(ReadOnlySpan<BudgetInput> input, ulong available)
    {
        var arrays = input.ToArray();
        var targets = new int[arrays.Length][];
        var minima = new int[arrays.Length];
        var drops = new PriorityQueue<int, (int MainClass, double Score, int Order)>();
        var headroom = new PriorityQueue<(int Array, int Mip), (ulong InverseBytes, int Order)>();
        ulong total = 0;
        void Add(int index) {
            var array = arrays[index]; var mip = minima[index];
            if (mip == array.TailBytes.Length - 1) return;
            double importance = 0;
            var mainClass = array.MainWanted is null ? 1 : 0;
            for (var layer = 0; layer < targets[index].Length; layer++) {
                if (targets[index][layer] != mip) continue;
                if (array.MainWanted is not null && array.MainWanted[layer] <= mip) mainClass = 1;
                var band = array.Priorities[layer];
                var weight = band == 0 ? 0 : System.Math.ScaleB(1, band - 1);
                // Retain equally important admitted detail, but let the next
                // priority band (twice the pixel area) preempt it.
                if (array.Admitted[layer] <= mip) weight *= 1.25;
                importance = System.Math.Max(importance, weight);
            }
            var saved = array.TailBytes[mip] - array.TailBytes[mip + 1];
            drops.Enqueue(index, (mainClass, -(double)saved / (1 + importance), index));
            headroom.Enqueue((index, mip), (ulong.MaxValue - array.TailBytes[mip + 1], index));
        }
        for (var index = 0; index < arrays.Length; index++) {
            var array = arrays[index];
            if (array.TailBytes.Length == 0 || array.Wanted.Length == 0
                || array.Wanted.Length != array.Admitted.Length || array.Wanted.Length != array.Priorities.Length
                || (array.MainWanted is not null && array.MainWanted.Length != array.Wanted.Length))
                throw new ArgumentException("Texture budget layer states differ.", nameof(input));
            for (var mip = 0; mip < array.TailBytes.Length; mip++)
                if (array.TailBytes[mip] == 0 || (mip > 0 && array.TailBytes[mip] > array.TailBytes[mip - 1]))
                    throw new ArgumentException("Texture tail costs must be positive and nonincreasing.", nameof(input));
            targets[index] = array.Wanted.ToArray();
            for (var layer = 0; layer < targets[index].Length; layer++)
                if ((uint)targets[index][layer] >= (uint)array.TailBytes.Length
                    || (uint)array.Admitted[layer] >= (uint)array.TailBytes.Length || (uint)array.Priorities[layer] > 63
                    || (array.MainWanted is not null && (uint)array.MainWanted[layer] >= (uint)array.TailBytes.Length))
                    throw new ArgumentOutOfRangeException(nameof(input));
            minima[index] = targets[index].Min();
            total = checked(total + array.TailBytes[minima[index]]);
            Add(index);
        }
        while (true) {
            ulong replacement = 0;
            while (headroom.TryPeek(out var entry, out _)) {
                if (minima[entry.Array] == entry.Mip) {
                    replacement = arrays[entry.Array].TailBytes[entry.Mip + 1]; break;
                }
                headroom.Dequeue();
            }
            // Preserve the existing old-tail replacement headroom. Actual
            // pending, uploading and retiring allocations are admitted by the
            // resource owner in addition to this eventual steady-state plan.
            if (total <= available && replacement <= available - total) return targets;
            if (!drops.TryDequeue(out var victim, out _))
                throw new InvalidOperationException("Bootstrap texture tails exceed the material budget.");
            var old = minima[victim]++;
            for (var layer = 0; layer < targets[victim].Length; layer++)
                if (targets[victim][layer] == old) targets[victim][layer]++;
            total -= arrays[victim].TailBytes[old] - arrays[victim].TailBytes[old + 1];
            Add(victim);
        }
    }

    internal static int TrimUpgradeMip(int resident, int desired, int requested)
        => System.Math.Max(requested, System.Math.Min(resident, desired));

    internal static ReadOnlyMemory<byte>[][] TrimUpload(ReadOnlyMemory<byte>[][] read, int readMip, int uploadMip,
        ReadOnlySpan<int> resident, ReadOnlySpan<int> published)
    {
        if (read.Length != resident.Length || read.Length != published.Length || uploadMip < readMip)
            throw new ArgumentException("Texture read and publication ranges differ.");
        var result = new ReadOnlyMemory<byte>[read.Length][];
        for (var layer = 0; layer < read.Length; layer++) {
            if (published[layer] < uploadMip)
                throw new ArgumentException("A published texture layer precedes its allocation.", nameof(published));
            // Copy only memory descriptors. The original read/result and its
            // complete decoded reservation remain owned until request join.
            result[layer] = read[layer].AsSpan(uploadMip - readMip).ToArray();
            for (var mip = 0; mip < result[layer].Length; mip++) {
                var level = uploadMip + mip;
                if (level < published[layer] || level >= resident[layer]) result[layer][mip] = default;
                else if (result[layer][mip].IsEmpty)
                    throw new InvalidOperationException("A published texture mip has neither resident storage nor completed input.");
            }
        }
        return result;
    }

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

    internal static bool IsObsolete(ReadOnlySpan<int> resident, ReadOnlySpan<int> desired, ReadOnlySpan<int> requested,
        int downgradeMip = -1)
    {
        if (resident.Length != desired.Length || resident.Length != requested.Length)
            throw new ArgumentException("Texture layer states differ.");
        var upgrading = false; var useful = false;
        for (var layer = 0; layer < resident.Length; layer++) {
            if (downgradeMip >= 0 && desired[layer] < downgradeMip) return true;
            if (requested[layer] > resident[layer] && desired[layer] < requested[layer]) return true;
            upgrading |= requested[layer] < resident[layer];
            useful |= requested[layer] < resident[layer] && desired[layer] < resident[layer];
        }
        return upgrading && !useful;
    }

    internal static void ValidateBootstrap(ulong uploadBytes, ulong maximumBytes)
    {
        if (uploadBytes > maximumBytes)
            throw new InvalidOperationException($"Initial texture upload requires {uploadBytes} bytes; bootstrap limit is {maximumBytes} bytes.");
    }
}
