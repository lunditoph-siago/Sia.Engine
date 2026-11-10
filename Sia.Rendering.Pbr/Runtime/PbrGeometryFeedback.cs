namespace Sia.Engine.Rendering.Pbr;

// Positive finite pixel-error ratios occupy the low 31 bits. The remaining bit
// makes atomicMax prefer camera demand without changing ordering within a view.
internal static class PbrGeometryFeedback
{
    internal const uint MainViewBit = 0x80000000u;

    internal static uint Encode(float priority, bool mainView)
    {
        if (!float.IsFinite(priority) || priority <= 0) throw new ArgumentOutOfRangeException(nameof(priority));
        return BitConverter.SingleToUInt32Bits(priority) | (mainView ? MainViewBit : 0);
    }

    internal static (float Priority, bool MainView) Decode(uint word)
    {
        var priority = BitConverter.UInt32BitsToSingle(word & ~MainViewBit);
        if (!float.IsFinite(priority) || priority <= 0) throw new InvalidDataException("GPU feedback priority is invalid.");
        return (priority, (word & MainViewBit) != 0);
    }
}
