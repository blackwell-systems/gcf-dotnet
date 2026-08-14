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
                    // Token shape follows domain (SPEC 2.3.2): a bare-integer JSON literal
                    // (no '.', 'e', or 'E') is an int64-domain integer. It MUST become an
                    // exact long, or raise out_of_range if it overflows int64 -- it must NOT
                    // fall back to GetDouble, which would silently approximate magnitudes
                    // past 2^53. A decimal/exponent literal is a double.
                    bool isBareInteger = raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0;
                    if (isBareInteger)
                    {
                        if (e.TryGetInt64(out var lv)) return lv;
                        throw new EncodeException(
                            "out_of_range: integer " + raw +
                            " is outside the canonical int64 domain [-9223372036854775808, 9223372036854775807]; " +
                            "model larger values as strings (SPEC 2.3.2)");
                    }
                    return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
