using System.Collections.Generic;
using System.Text.Json;
using BlackwellSystems.Gcf;

namespace BlackwellSystems.Gcf.Tests
{
    /// <summary>Converts fixture JSON into the GCF native value model (order-preserving).</summary>
    internal static class ConformanceInput
    {
        public static object? FromJson(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var m = new OrderedMap();
                    foreach (var p in e.EnumerateObject()) m[p.Name] = FromJson(p.Value);
                    return m;
                case JsonValueKind.Array:
                    var l = new List<object?>();
                    foreach (var it in e.EnumerateArray()) l.Add(FromJson(it));
                    return l;
                case JsonValueKind.String:
                    return e.GetString();
                case JsonValueKind.Number:
                    var raw = e.GetRawText();
                    // Integer iff no '.', 'e', or 'E' in the source and it fits in long.
                    if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0 && e.TryGetInt64(out var lv))
                        return lv;
                    return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
