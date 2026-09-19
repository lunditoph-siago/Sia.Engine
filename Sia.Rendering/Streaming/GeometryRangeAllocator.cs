namespace Sia.Engine.Rendering;

// The consumer must finish using a range before releasing it for reuse.
public sealed class GeometryRangeAllocator
{
    public readonly record struct Range(int Offset, int Length);
    private readonly List<Range> _free;
    private readonly int _start, _end;
    public GeometryRangeAllocator(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _start = offset; _end = checked(offset + length);
        _free = length > 0 ? [new(offset, length)] : [];
    }
    public Range? Allocate(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        for (var i = 0; i < _free.Count; i++) {
            var range = _free[i];
            if (range.Length < length) continue;
            if (range.Length == length) _free.RemoveAt(i);
            else _free[i] = new(range.Offset + length, range.Length - length);
            return new(range.Offset, length);
        }
        return null;
    }
    public void Release(Range range)
    {
        var end = (long)range.Offset + range.Length;
        if (range.Length <= 0 || range.Offset < _start || end > _end)
            throw new ArgumentOutOfRangeException(nameof(range));
        var index = _free.FindIndex(r => r.Offset > range.Offset);
        if (index < 0) index = _free.Count;
        if ((index > 0 && _free[index - 1].Offset + _free[index - 1].Length > range.Offset)
            || (index < _free.Count && end > _free[index].Offset))
            throw new ArgumentException("The released range overlaps free storage.", nameof(range));
        _free.Insert(index, range);
        for (var i = System.Math.Max(0, index - 1); i + 1 < _free.Count;) {
            var a = _free[i]; var b = _free[i + 1];
            if (a.Offset + a.Length == b.Offset) { _free[i] = new(a.Offset, a.Length + b.Length); _free.RemoveAt(i + 1); }
            else i++;
        }
    }
}
