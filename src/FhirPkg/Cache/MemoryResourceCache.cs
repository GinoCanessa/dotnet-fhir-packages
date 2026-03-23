// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Models;

namespace FhirPkg.Cache;

/// <summary>
/// Thread-safe, LRU (Least Recently Used) in-memory cache for parsed FHIR resources.
/// Provides O(1) lookup, insertion, and eviction using a <see cref="LinkedList{T}"/>
/// combined with a <see cref="Dictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This cache is designed to sit on top of the disk-based package cache to avoid
/// repeated deserialization of frequently-accessed resources.
/// </para>
/// <para>
/// The <see cref="SafeMode"/> setting controls whether returned values are the
/// original cached references or defensive copies:
/// <list type="bullet">
///   <item><description><see cref="SafeMode.Off"/>: Returns the cached reference directly. Fastest, but callers must not mutate.</description></item>
///   <item><description><see cref="SafeMode.Clone"/>: Returns a deep clone via JSON serialization round-trip.</description></item>
///   <item><description><see cref="SafeMode.Freeze"/>: Returns a deep clone (functionally equivalent to Clone in .NET).</description></item>
/// </list>
/// </para>
/// </remarks>
public class MemoryResourceCache
{
    private readonly int _maxEntries;
    private readonly SafeMode _safeMode;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lruList = new();

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of <see cref="MemoryResourceCache"/> with the specified capacity and safety mode.
    /// </summary>
    /// <param name="maxEntries">
    /// Maximum number of entries before LRU eviction occurs. Must be at least 1. Default is 200.
    /// </param>
    /// <param name="safeMode">
    /// Controls whether cached values are returned directly or as defensive copies.
    /// Default is <see cref="SafeMode.Off"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxEntries"/> is less than 1.</exception>
    public MemoryResourceCache(int maxEntries = 200, SafeMode safeMode = SafeMode.Off)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        _maxEntries = maxEntries;
        _safeMode = safeMode;
        _map = new Dictionary<string, LinkedListNode<CacheEntry>>(maxEntries, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the current number of entries in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Retrieves a cached value by key, promoting it to most-recently-used.
    /// Returns <c>null</c> if the key is not found or the stored type does not match <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected type of the cached value. Must be a reference type.</typeparam>
    /// <param name="key">The cache key to look up.</param>
    /// <returns>
    /// The cached value (or a clone, depending on <see cref="SafeMode"/>),
    /// or <c>null</c> if not found or type mismatch.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is <c>null</c>.</exception>
    public T? Get<T>(string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node))
                return null;

            // Promote to most-recently-used (move to front of list)
            _lruList.Remove(node);
            _lruList.AddFirst(node);

            if (node.Value.Value is not T typed)
                return null;

            return MaybeClone(typed);
        }
    }

    /// <summary>
    /// Stores a value in the cache under the specified key, evicting the
    /// least-recently-used entry if the cache is at capacity.
    /// If the key already exists, the value is updated and promoted.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache. Must be a reference type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="value"/> is <c>null</c>.</exception>
    public void Set<T>(string key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            // If the key already exists, remove the old node
            if (_map.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                _map.Remove(key);
            }

            // Evict LRU entries until we have room
            while (_map.Count >= _maxEntries && _lruList.Last is not null)
            {
                var lruNode = _lruList.Last!;
                _map.Remove(lruNode.Value.Key);
                _lruList.RemoveLast();
            }

            // Store the value (clone on write for Clone/Freeze modes)
            var storedValue = _safeMode is SafeMode.Off ? value : DeepClone(value);
            var entry = new CacheEntry(key, storedValue);
            var newNode = _lruList.AddFirst(entry);
            _map[key] = newNode;
        }
    }

    /// <summary>
    /// Removes all entries from the cache.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lruList.Clear();
        }
    }

    /// <summary>
    /// Returns the value directly or a deep clone, depending on the configured <see cref="SafeMode"/>.
    /// </summary>
    private T MaybeClone<T>(T value) where T : class
    {
        return _safeMode switch
        {
            SafeMode.Off => value,
            SafeMode.Clone or SafeMode.Freeze => DeepClone(value),
            _ => value
        };
    }

    /// <summary>
    /// Creates a deep clone of an object. Uses <see cref="ICloneable"/> when available,
    /// falling back to JSON serialization round-trip for types that don't implement it.
    /// </summary>
    private static T DeepClone<T>(T value) where T : class
    {
        if (value is ICloneable cloneable)
            return (T)cloneable.Clone();

        var json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), s_jsonOptions);
        return (T)JsonSerializer.Deserialize(json, value.GetType(), s_jsonOptions)!;
    }

    /// <summary>
    /// Internal record to associate cache keys with their values in the linked list.
    /// </summary>
    private sealed record CacheEntry(string Key, object Value);
}
