using Sia;

namespace Sia.Engine.Configuration;

/// <summary>Increasing override priority for effective configuration values.</summary>
public enum ConfigurationLayer
{
    Engine,
    Project,
    Platform,
    Quality,
    User,
    Session
}

internal readonly record struct ConfigurationOverride(bool HasValue, object? Value);

/// <summary>A batch of overrides for a single configuration layer.</summary>
public sealed class ConfigurationPatch
{
    private readonly Dictionary<ConfigurationKey, ConfigurationOverride> _entries = [];
    internal ConfigurationSchema Schema { get; }
    internal Dictionary<ConfigurationKey, ConfigurationOverride> Entries => _entries;

    public ConfigurationPatch(ConfigurationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
    }

    public ConfigurationPatch Set<T>(ConfigurationKey<T> key, T value)
    {
        Schema.RequireKey(key);
        key.Validate(value);
        _entries[key] = new(true, value);
        return this;
    }

    /// <summary>Set a value from a data adapter after checking it against the registered key type.</summary>
    public ConfigurationPatch SetValue(ConfigurationKey key, object? value)
    {
        Schema.RequireKey(key);
        key.ValidateValue(value);
        _entries[key] = new(true, value);
        return this;
    }

    public ConfigurationPatch Clear(ConfigurationKey key)
    {
        Schema.RequireKey(key);
        _entries[key] = default;
        return this;
    }
}

/// <summary>
/// Resolves overrides on its owning thread and publishes the latest effective snapshot for readers.
/// Consumers that apply changes asynchronously can retain their own last applied snapshot.
/// </summary>
public sealed class ConfigurationStore : IAddon
{
    private const int LayerCount = (int)ConfigurationLayer.Session + 1;
    private readonly ConfigurationOverride[][] _layers = new ConfigurationOverride[LayerCount][];
    private ConfigurationSnapshot _active;

    public ConfigurationSchema Schema { get; }
    public ConfigurationSnapshot Active => Volatile.Read(ref _active);

    public ConfigurationStore(ConfigurationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        schema.Seal();
        var defaults = new object?[schema.Keys.Count];
        for (var i = 0; i < defaults.Length; i++) defaults[i] = schema.Keys[i].Default;
        for (var i = 0; i < _layers.Length; i++) _layers[i] = new ConfigurationOverride[defaults.Length];
        _active = new(schema, 0, defaults);
    }

    /// <summary>Set one layer override. Returns true when the effective configuration changes.</summary>
    public bool Set<T>(ConfigurationLayer layer, ConfigurationKey<T> key, T value)
    {
        Schema.RequireKey(key);
        key.Validate(value);
        var layerIndex = GetLayerIndex(layer);
        var previous = _layers[layerIndex][key.Index];
        if (previous.HasValue && EqualityComparer<T>.Default.Equals((T)previous.Value!, value)) return false;
        return ApplyOne(layerIndex, key, new(true, value));
    }

    /// <summary>Remove one layer override. Returns true when the effective configuration changes.</summary>
    public bool Clear(ConfigurationLayer layer, ConfigurationKey key)
    {
        Schema.RequireKey(key);
        return ApplyOne(GetLayerIndex(layer), key, default);
    }

    /// <summary>Apply a batch atomically. Returns true when the effective configuration changes.</summary>
    public bool Apply(ConfigurationLayer layer, ConfigurationPatch patch) => Apply(layer, patch, replace: false);

    /// <summary>Replace all overrides in the layer, including keys omitted from the new patch.</summary>
    public bool Replace(ConfigurationLayer layer, ConfigurationPatch patch) => Apply(layer, patch, replace: true);

    private bool Apply(ConfigurationLayer layer, ConfigurationPatch patch, bool replace)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!ReferenceEquals(patch.Schema, Schema))
            throw new ArgumentException("The configuration patch belongs to a different schema.", nameof(patch));
        var layerIndex = GetLayerIndex(layer);
        object?[]? changed = null;
        var entries = _layers[layerIndex];
        foreach (var (key, entry) in patch.Entries) PrepareChange(key, entry);
        if (replace) {
            var keys = Schema.Keys;
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].HasValue && !patch.Entries.ContainsKey(keys[i])) PrepareChange(keys[i], default);
        }

        var next = changed is null ? null : new ConfigurationSnapshot(Schema, checked(_active.Revision + 1), changed);
        if (replace) Array.Clear(entries);
        foreach (var (key, entry) in patch.Entries) entries[key.Index] = entry;
        if (next is null) return false;
        Volatile.Write(ref _active, next);
        return true;

        void PrepareChange(ConfigurationKey key, ConfigurationOverride entry)
        {
            var value = Resolve(layerIndex, key, entry);
            if (key.ValuesEqual(_active.GetValue(key), value)) return;
            changed ??= _active.CopyValues();
            changed[key.Index] = value;
        }
    }

    private bool ApplyOne(int layer, ConfigurationKey key, ConfigurationOverride entry)
    {
        var value = Resolve(layer, key, entry);
        if (key.ValuesEqual(_active.GetValue(key), value)) {
            _layers[layer][key.Index] = entry;
            return false;
        }

        var values = _active.CopyValues();
        values[key.Index] = value;
        var next = new ConfigurationSnapshot(Schema, checked(_active.Revision + 1), values);
        _layers[layer][key.Index] = entry;
        Volatile.Write(ref _active, next);
        return true;
    }

    private object? Resolve(int updatedLayer, ConfigurationKey key, ConfigurationOverride updated)
    {
        for (var i = _layers.Length - 1; i >= 0; i--) {
            var entry = i == updatedLayer ? updated : _layers[i][key.Index];
            if (entry.HasValue) return entry.Value;
        }
        return key.Default;
    }

    private static int GetLayerIndex(ConfigurationLayer layer)
    {
        var index = (int)layer;
        if ((uint)index >= LayerCount) throw new ArgumentOutOfRangeException(nameof(layer));
        return index;
    }
}
