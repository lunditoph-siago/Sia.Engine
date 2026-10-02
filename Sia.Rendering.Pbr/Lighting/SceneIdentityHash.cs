using System.IO.Hashing;

namespace Sia.Engine.Rendering.Pbr;

internal sealed class SceneIdentityHash
{
    private readonly XxHash128 _hash = new();
    private readonly byte[] _buffer = new byte[4096];
    private int _count;

    public void AppendData(ReadOnlySpan<byte> data)
    {
        if (_count != 0) {
            var size = System.Math.Min(_buffer.Length - _count, data.Length);
            data[..size].CopyTo(_buffer.AsSpan(_count));
            _count += size;
            data = data[size..];
            if (_count != _buffer.Length) return;
            _hash.Append(_buffer);
            _count = 0;
        }
        if (data.Length >= _buffer.Length) {
            _hash.Append(data);
            return;
        }
        data.CopyTo(_buffer);
        _count = data.Length;
    }

    public byte[] Finish()
    {
        if (_count != 0) _hash.Append(_buffer.AsSpan(0, _count));
        _count = 0;
        var identity = new byte[32];
        "SIA-XXH128-v1"u8.CopyTo(identity);
        _hash.GetHashAndReset(identity.AsSpan(16));
        return identity;
    }
}
