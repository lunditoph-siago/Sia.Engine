using System.Diagnostics.CodeAnalysis;

namespace Sia.Engine.Configuration;

/// <summary>Registers the stable keys that a configuration store can publish.</summary>
public sealed class ConfigurationSchema
{
    private readonly Dictionary<string, ConfigurationKey> _keys = new(StringComparer.Ordinal);
    private readonly List<ConfigurationKey> _ordered = [];
    private int _operation;
    private bool _sealed;

    public IReadOnlyList<ConfigurationKey> Keys { get; }

    public ConfigurationSchema() => Keys = _ordered.AsReadOnly();

    /// <summary>Use immutable values and deterministic validators for configuration keys.</summary>
    public ConfigurationKey<T> Register<T>(string name, T defaultValue, Func<T, bool>? validate = null)
    {
        using var operation = new ConfigurationOperation(ref _operation);
        if (_sealed) throw new InvalidOperationException("The configuration schema is already in use.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name != name.Trim() || name.Any(char.IsControl))
            throw new ArgumentException("A configuration key must have a stable name without surrounding whitespace or control characters.", nameof(name));
        if (_keys.ContainsKey(name))
            throw new ArgumentException($"Configuration key '{name}' is already registered.", nameof(name));

        var key = new ConfigurationKey<T>(this, _ordered.Count, name, defaultValue, validate);
        key.Validate(defaultValue);
        _keys.Add(name, key);
        _ordered.Add(key);
        return key;
    }

    public bool TryGetKey(string name, [NotNullWhen(true)] out ConfigurationKey? key) => _keys.TryGetValue(name, out key);

    internal void Seal()
    {
        if (Volatile.Read(ref _sealed)) return;
        using var operation = new ConfigurationOperation(ref _operation);
        Volatile.Write(ref _sealed, true);
    }

    internal void RequireKey(ConfigurationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!ReferenceEquals(key.Schema, this))
            throw new ArgumentException($"Configuration key '{key.Name}' belongs to a different schema.", nameof(key));
    }
}

// Validation and value comparison can invoke caller code. An exclusive scope
// protects the current operation without tying it to a permanent thread.
internal readonly ref struct ConfigurationOperation
{
    private readonly ref int _state;

    internal ConfigurationOperation(ref int state)
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("A configuration operation on this instance is already in progress.");
        _state = ref state;
    }

    public void Dispose() => Volatile.Write(ref _state, 0);
}

public abstract class ConfigurationKey
{
    internal ConfigurationSchema Schema { get; }
    internal int Index { get; }
    public string Name { get; }
    public abstract Type ValueType { get; }
    internal abstract object? Default { get; }

    internal ConfigurationKey(ConfigurationSchema schema, int index, string name)
    {
        Schema = schema;
        Index = index;
        Name = name;
    }

    internal abstract void ValidateValue(object? value);
    internal abstract bool ValuesEqual(object? left, object? right);
}

public sealed class ConfigurationKey<T> : ConfigurationKey
{
    private readonly Func<T, bool>? _validate;

    public T DefaultValue { get; }
    public override Type ValueType => typeof(T);
    internal override object? Default => DefaultValue;

    internal ConfigurationKey(ConfigurationSchema schema, int index, string name, T defaultValue, Func<T, bool>? validate)
        : base(schema, index, name)
    {
        DefaultValue = defaultValue;
        _validate = validate;
    }

    internal void Validate(T value)
    {
        if (_validate?.Invoke(value) == false)
            throw new ArgumentException($"Invalid value for configuration key '{Name}'.", nameof(value));
    }

    internal override void ValidateValue(object? value)
    {
        if (value is T typed) Validate(typed);
        else if (value is null && default(T) is null) Validate(default!);
        else throw new ArgumentException($"Configuration key '{Name}' requires a value of type {ValueType.Name}.", nameof(value));
    }

    internal override bool ValuesEqual(object? left, object? right) =>
        EqualityComparer<T>.Default.Equals((T)left!, (T)right!);
}
