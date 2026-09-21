using System.Linq;
using Sia.Engine.Rendering;

namespace Sia.Engine.Rendering.Benchmarks;

internal static class StreamBlockVerification
{
    public static void Run()
    {
        var random = new Random(20260921);

        // Round-trip through the array-returning overload still works unchanged.
        var source = new byte[257 * 1024];
        random.NextBytes(source);
        var encoded = SceneStreamBlock.Encode(source);
        var decoded = SceneStreamBlock.Decode(encoded);
        if (!decoded.AsSpan().SequenceEqual(source)) throw new InvalidOperationException("Array-returning decode did not round-trip.");

        // The destination-writing overload must decode into an arbitrary offset/length slice of a larger
        // buffer -- the exact shape ReadPartsAsync uses to assemble several parts without a temp copy --
        // and report the true decoded length, independent of how much spare room the destination has.
        var combined = new byte[source.Length + 4096];
        var written = SceneStreamBlock.Decode(encoded, combined.AsSpan(1024, source.Length + 2048));
        if (written != source.Length) throw new InvalidOperationException($"Expected {source.Length} decoded bytes, got {written}.");
        if (!combined.AsSpan(1024, source.Length).SequenceEqual(source)) throw new InvalidOperationException("Destination-writing decode wrote the wrong bytes.");
        if (combined.AsSpan(0, 1024).ToArray().Any(b => b != 0) || combined.AsSpan(1024 + source.Length).ToArray().Any(b => b != 0))
            throw new InvalidOperationException("Destination-writing decode wrote outside its declared slice.");

        // Several parts concatenated into one buffer -- the real ReadPartsAsync shape -- must each land at
        // the right offset and the reported lengths must sum to the whole.
        var parts = new byte[3][];
        for (var i = 0; i < parts.Length; i++) { parts[i] = new byte[4096 + i * 977]; random.NextBytes(parts[i]); }
        var assembled = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts) {
            var partEncoded = SceneStreamBlock.Encode(part);
            offset += SceneStreamBlock.Decode(partEncoded, assembled.AsSpan(offset));
        }
        if (offset != assembled.Length) throw new InvalidOperationException("Assembled part lengths did not sum to the combined buffer.");
        var expected = parts.SelectMany(p => p).ToArray();
        if (!assembled.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Assembled parts landed at the wrong offsets.");

        // A destination too small for the declared decoded length must still be rejected, the same as the
        // array-returning overload's maximumLength bound.
        try {
            SceneStreamBlock.Decode(encoded, new byte[source.Length - 1]);
            throw new InvalidOperationException("An undersized destination did not throw.");
        } catch (InvalidDataException) { /* Expected. */ }

        // Existing corruption/truncation checks must still reject invalid input after the header/decode split.
        var truncated = encoded[..^1];
        try {
            SceneStreamBlock.Decode(truncated);
            throw new InvalidOperationException("Truncated input did not throw.");
        } catch (InvalidDataException) { /* Expected. */ }
        var corrupted = (byte[])encoded.Clone(); corrupted[0] ^= 0xFF;
        try {
            SceneStreamBlock.Decode(corrupted);
            throw new InvalidOperationException("A corrupted header did not throw.");
        } catch (InvalidDataException) { /* Expected. */ }

        Console.WriteLine("Scene stream block verification passed: array round-trip, destination-writing decode, multi-part assembly offsets, undersized destination rejection, and corruption/truncation rejection.");
    }
}
