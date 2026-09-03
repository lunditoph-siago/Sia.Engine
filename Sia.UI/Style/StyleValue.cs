namespace Sia.UI;

public readonly record struct StyleValue<T>
    where T : struct
{
    private readonly T _value;

    public StyleValue(T value)
    {
        _value = value;
        IsSpecified = true;
    }

    public bool IsSpecified { get; }

    public T Value => IsSpecified
        ? _value
        : throw new InvalidOperationException("The style value is unspecified.");

    public static implicit operator StyleValue<T>(T value) => new(value);
}
