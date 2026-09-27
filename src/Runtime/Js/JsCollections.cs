using System;
using System.Collections;
using System.Collections.Generic;

namespace Fluitown.Runtime;

/// <summary>
/// ECMAScript <c>Map</c>: iteration follows insertion order, deleting during iteration skips the entry,
/// and entries added during iteration are visited. <see cref="Dictionary{TKey,TValue}"/> does not
/// guarantee any of this once entries are removed, and generator passes that iterate a Map to decide
/// what to carve must see exactly the order the original saw.
/// </summary>
public sealed class JsMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    private readonly Dictionary<TKey, int> _index;
    private readonly List<TKey> _keys = new();
    private readonly List<TValue> _values = new();
    private readonly List<bool> _alive = new();
    private int _count;
    // Running iterators (see Compact): deleted slots stay as tombstones while anyone iterates, exactly like JS.
    private int _iterators;

    public JsMap() => _index = new Dictionary<TKey, int>();

    public int size => _count;

    public bool has(TKey key) => _index.ContainsKey(key);

    public TValue? get(TKey key) => _index.TryGetValue(key, out int i) ? _values[i] : default;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_index.TryGetValue(key, out int i))
        {
            value = _values[i];
            return true;
        }
        value = default!;
        return false;
    }

    public JsMap<TKey, TValue> set(TKey key, TValue value)
    {
        if (_index.TryGetValue(key, out int i))
        {
            _values[i] = value;
            return this;
        }
        CompactIfIdle();
        _index[key] = _keys.Count;
        _keys.Add(key);
        _values.Add(value);
        _alive.Add(true);
        _count++;
        return this;
    }

    /// <summary>
    /// Long-lived maps with churn (the terrain tile cache adds and deletes keys for a whole session) would otherwise
    /// keep every tombstone forever and make each iteration proportional to the map's history. Tombstones are only
    /// observable by a running iterator, so they are dropped when none is running; order is unaffected.
    /// </summary>
    private void CompactIfIdle()
    {
        int dead = _keys.Count - _count;
        if (_iterators != 0 || dead < 64 || dead < _count) return;
        int write = 0;
        for (int read = 0; read < _keys.Count; read++)
        {
            if (!_alive[read]) continue;
            _keys[write] = _keys[read];
            _values[write] = _values[read];
            _alive[write] = true;
            _index[_keys[write]] = write;
            write++;
        }
        _keys.RemoveRange(write, _keys.Count - write);
        _values.RemoveRange(write, _values.Count - write);
        _alive.RemoveRange(write, _alive.Count - write);
    }

    public bool delete(TKey key)
    {
        if (!_index.TryGetValue(key, out int i)) return false;
        _index.Remove(key);
        _alive[i] = false;
        _values[i] = default!;
        _count--;
        return true;
    }

    public void clear()
    {
        // A cleared Map keeps no live entries; iterators already running see nothing further.
        for (int i = 0; i < _alive.Count; i++) _alive[i] = false;
        _index.Clear();
        _count = 0;
    }

    public TValue this[TKey key]
    {
        get => _values[_index[key]];
        set => set(key, value);
    }

    public IEnumerable<TKey> keys()
    {
        _iterators++;
        try
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_alive[i]) yield return _keys[i];
        }
        finally { _iterators--; }
    }

    /// <summary>
    /// The live values in insertion order. <c>foreach</c> binds to <see cref="ValueView.GetEnumerator"/>, a struct that
    /// allocates nothing (per-frame tile loops); LINQ and other interface consumers get the
    /// iterator below. Both follow the same Map rules.
    /// </summary>
    public ValueView values() => _valueView ??= new ValueView(this);

    private ValueView? _valueView;

    public sealed class ValueView : IEnumerable<TValue>
    {
        private readonly JsMap<TKey, TValue> _map;
        internal ValueView(JsMap<TKey, TValue> map) => _map = map;
        public ValueEnumerator GetEnumerator() => new(_map);
        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => _map.valueIterator().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _map.valueIterator().GetEnumerator();
    }

    /// <summary>The iterator of <see cref="valueIterator"/> as a struct: counts as a running iterator (tombstones stay)
    /// from its creation until it is exhausted or disposed; visits entries added meanwhile.</summary>
    public struct ValueEnumerator : IEnumerator<TValue>
    {
        private readonly JsMap<TKey, TValue> _map;
        private int _index;
        private bool _running;
        private TValue _current;

        internal ValueEnumerator(JsMap<TKey, TValue> map)
        {
            _map = map;
            _index = -1;
            _current = default!;
            _running = true;
            map._iterators++;
        }

        public readonly TValue Current => _current;
        readonly object? IEnumerator.Current => _current;

        public bool MoveNext()
        {
            if (!_running) return false;
            while (++_index < _map._keys.Count)
                if (_map._alive[_index])
                {
                    _current = _map._values[_index];
                    return true;
                }
            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            _map._iterators--;
        }

        public void Reset() => throw new NotSupportedException();
    }

    private IEnumerable<TValue> valueIterator()
    {
        _iterators++;
        try
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_alive[i]) yield return _values[i];
        }
        finally { _iterators--; }
    }

    public IEnumerable<KeyValuePair<TKey, TValue>> entries()
    {
        _iterators++;
        try
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_alive[i]) yield return new KeyValuePair<TKey, TValue>(_keys[i], _values[i]);
        }
        finally { _iterators--; }
    }

    /// <summary><c>foreach</c> over the entries without an allocated iterator (see <see cref="values"/>); interface
    /// consumers get <see cref="entries"/>.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => entries().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => entries().GetEnumerator();

    /// <summary><see cref="entries"/> as a struct, with the rules of <see cref="ValueEnumerator"/>.</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly JsMap<TKey, TValue> _map;
        private int _index;
        private bool _running;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(JsMap<TKey, TValue> map)
        {
            _map = map;
            _index = -1;
            _current = default;
            _running = true;
            map._iterators++;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;
        readonly object IEnumerator.Current => _current;

        public bool MoveNext()
        {
            if (!_running) return false;
            while (++_index < _map._keys.Count)
                if (_map._alive[_index])
                {
                    _current = new KeyValuePair<TKey, TValue>(_map._keys[_index], _map._values[_index]);
                    return true;
                }
            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            _map._iterators--;
        }

        public void Reset() => throw new NotSupportedException();
    }
}

/// <summary>ECMAScript <c>Set</c> with insertion-ordered iteration (see <see cref="JsMap{TKey,TValue}"/>).</summary>
public sealed class JsSet<T> : IEnumerable<T> where T : notnull
{
    private readonly Dictionary<T, int> _index;
    private readonly List<T> _items = new();
    private readonly List<bool> _alive = new();
    private int _count;

    public JsSet() => _index = new Dictionary<T, int>();

    public JsSet(IEnumerable<T> items) : this()
    {
        foreach (var item in items) add(item);
    }

    public int size => _count;

    public bool has(T item) => _index.ContainsKey(item);

    public JsSet<T> add(T item)
    {
        if (_index.ContainsKey(item)) return this;
        _index[item] = _items.Count;
        _items.Add(item);
        _alive.Add(true);
        _count++;
        return this;
    }

    public bool delete(T item)
    {
        if (!_index.TryGetValue(item, out int i)) return false;
        _index.Remove(item);
        _alive[i] = false;
        _count--;
        return true;
    }

    public void clear()
    {
        for (int i = 0; i < _alive.Count; i++) _alive[i] = false;
        _index.Clear();
        _count = 0;
    }

    public IEnumerable<T> values()
    {
        for (int i = 0; i < _items.Count; i++)
            if (_alive[i]) yield return _items[i];
    }

    public List<T> ToList()
    {
        var list = new List<T>(_count);
        for (int i = 0; i < _items.Count; i++)
            if (_alive[i]) list.Add(_items[i]);
        return list;
    }

    public IEnumerator<T> GetEnumerator() => values().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
