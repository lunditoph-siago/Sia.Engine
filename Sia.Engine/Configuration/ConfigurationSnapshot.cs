namespace Sia.Engine.Configuration;

/// <summary>Immutable effective configuration that consumers can retain across updates.</summary>
public sealed class ConfigurationSnapshot
{
    private readonly ConfigurationSchema _schema;
    private readonly object?[] _values;

    /// <summary>Effective-value revision within the publishing store.</summary>
    public long Revision { get; }

    internal ConfigurationSnapshot(ConfigurationSchema schema, long revision, object?[] values)
    {
        _schema = schema;
        Revision = revision;
        _values = values;
    }

    public T Get<T>(ConfigurationKey<T> key) => (T)GetValue(key)!;

    public object? GetValue(ConfigurationKey key)
    {
        _schema.RequireKey(key);
        return _values[key.Index];
    }

    /// <summary>Compare one value across snapshots of the same schema, even when revisions were skipped.</summary>
    public bool HasChanged(ConfigurationSnapshot previous, ConfigurationKey key)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!ReferenceEquals(previous._schema, _schema))
            throw new ArgumentException("Cannot compare snapshots from different configuration schemas.", nameof(previous));
        _schema.RequireKey(key);
        return !ReferenceEquals(previous, this) && !key.ValuesEqual(previous._values[key.Index], _values[key.Index]);
    }

    internal object?[] CopyValues() => (object?[])_values.Clone();
}
