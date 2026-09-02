namespace Sia.Asset;

public readonly record struct TypedPath<T>(string Value)
{
    public static TypedPath<T> From(string value)
        => new(value);

    public static implicit operator string(TypedPath<T> path) => path.Value;
    public static implicit operator TypedPath<T>(string path) => new(path);
}