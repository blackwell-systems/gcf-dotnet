using System.Collections;
using System.Collections.Generic;

namespace BlackwellSystems.Gcf
{
    /// <summary>
    /// An insertion-ordered string-keyed map. GCF preserves object key order, so
    /// the native value model uses this in place of a plain Dictionary. Values are
    /// the GCF native model: null, bool, long, double, string, OrderedMap, or
    /// IList&lt;object?&gt;.
    /// </summary>
    public sealed class OrderedMap : IEnumerable<KeyValuePair<string, object?>>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, object?> _values = new Dictionary<string, object?>();

        public int Count => _keys.Count;

        public IReadOnlyList<string> Keys => _keys;

        public object? this[string key]
        {
            get => _values[key];
            set
            {
                if (!_values.ContainsKey(key))
                {
                    _keys.Add(key);
                }
                _values[key] = value;
            }
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        /// <summary>Returns the value for key, or null when the key is absent.</summary>
        public object? GetOrNull(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

        public void Add(string key, object? value)
        {
            this[key] = value;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var k in _keys)
            {
                yield return new KeyValuePair<string, object?>(k, _values[k]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Thrown when GCF decoding fails.</summary>
    public sealed class DecodeException : System.Exception
    {
        public DecodeException(string message) : base(message) { }
    }
}
