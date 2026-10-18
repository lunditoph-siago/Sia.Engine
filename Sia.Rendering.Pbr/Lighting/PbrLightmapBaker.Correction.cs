using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapBaker
{
    /// <summary>
    /// Integrates both caller-retained domains at identical positions/directions and subtracts their
    /// unquantized coefficients. This baker represents live transport; reference owns the frozen bake
    /// scene/lighting. No assets, IO, GPU state or output publication are owned here. Both bakers must
    /// be used serially; failure/cancellation leaves caller output unchanged. Scratch is preallocated.
    /// </summary>
    public void BakeCorrection(PbrLightmapBaker reference, int edge, ReadOnlySpan<PbrLightmapSurfaceTexel> surfaces,
        Span<PbrLightmapCoefficients> output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference); ValidateShape(edge, surfaces.Length);
        if (output.Length != surfaces.Length) throw new ArgumentException("Correction output must match its surface page.", nameof(output));
        // Light/sky values can differ; the estimator's geometry and ray domain must remain paired.
        if (_settings.Samples != reference._settings.Samples
            || BitConverter.SingleToInt32Bits(_settings.RayBias) != BitConverter.SingleToInt32Bits(reference._settings.RayBias)
            || BitConverter.SingleToInt32Bits(_settings.MaximumDistance) != BitConverter.SingleToInt32Bits(reference._settings.MaximumDistance))
            throw new ArgumentException("Live/reference integration must share sample count, ray bias and maximum distance.", nameof(reference));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Lighting production must be serialized.");
        var referenceHeld = false;
        try {
            if (!ReferenceEquals(reference, this)) {
                if (Interlocked.Exchange(ref reference._busy, 1) != 0)
                    throw new InvalidOperationException("Reference lighting production must be serialized.");
                referenceHeld = true;
                reference.Integrate(surfaces, cancellationToken);
            }
            Integrate(surfaces, cancellationToken);
            for (var at = 0; at < surfaces.Length; at++) {
                cancellationToken.ThrowIfCancellationRequested();
                _scratch[at] = PbrLightmapCoefficients.Difference(_scratch[at], reference._scratch[at]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _scratch.AsSpan(0, output.Length).CopyTo(output);
        }
        finally {
            if (referenceHeld) Volatile.Write(ref reference._busy, 0);
            Volatile.Write(ref _busy, 0);
        }
    }
}
