using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BlackwellSystems.Gcf;
using Tomlyn;
using Tomlyn.Model;
using Xunit;
using YamlDotNet.Serialization;

namespace BlackwellSystems.Gcf.Tests
{
    /// <summary>
    /// Cross-format fuzz. GCF encodes any structured data, not just JSON, so this
    /// generates random structured values, round-trips them through JSON, YAML, TOML,
    /// and CSV (each format's own library), and asserts GCF losslessly round-trips the
    /// format-canonical value: DecodeGeneric(EncodeGeneric(v)) == v. Mirrors the
    /// gcf-rust multiformat fuzz. The library stays zero-dependency; the format libs
    /// are test-only.
    /// </summary>
    public class MultiFormatFuzzTests
    {
        private static int Iterations =>
            int.TryParse(Environment.GetEnvironmentVariable("GCF_FUZZ_N"), out var n) && n > 0 ? Math.Min(n, 2000) : 400;
        private static int Seed =>
            int.TryParse(Environment.GetEnvironmentVariable("GCF_FUZZ_SEED"), out var s) ? s : 1;

        private Random _rng = new Random(Seed);

        // Simple, format-representable keys/scalars (the adversarial delimiter alphabet
        // is covered by the native FuzzTests; here we test cross-format interop).
        private static readonly string[] KeyPool = { "id", "name", "a", "b", "x1", "key", "nested", "tags", "value", "café", "dept" };
        private object? Scalar(bool allowNull)
        {
            int r = _rng.Next(allowNull ? 6 : 5);
            switch (r)
            {
                case 0: return WordString();
                case 1: return (long)_rng.Next(-1000, 1000);
                case 2: return Math.Round(_rng.NextDouble() * 200 - 100, 3);
                case 3: return _rng.Next(2) == 0;
                case 4: return WordString();
                default: return null;
            }
        }
        private string WordString()
        {
            string[] w = { "alpha", "b eta", "gam,ma", "shipped", "pending", "café", "x-1", "true", "42", "", "a\"b" };
            return w[_rng.Next(w.Length)];
        }

        private void GcfLossless(object? v, string ctx)
        {
            var wire = Gcf.EncodeGeneric(v);
            var back = Gcf.DecodeGeneric(wire);
            if (!GenericConformanceTests.DeepEqual(v, back))
                throw new Xunit.Sdk.XunitException($"[{ctx}] GCF not lossless on format-canonical value\nwire:\n{wire}");
        }

        // ---------- JSON ----------
        [Fact]
        public void Json()
        {
            _rng = new Random(Seed);
            for (int i = 0; i < Iterations; i++)
            {
                var v = GenValue(4, allowNull: true);
                var canonical = FromJsonElement(JsonDocument.Parse(NativeToJson(v)).RootElement);
                GcfLossless(canonical, $"json#{i}");
            }
        }

        // ---------- YAML ----------
        [Fact]
        public void Yaml()
        {
            _rng = new Random(Seed ^ 0x1);
            var ser = new SerializerBuilder().Build();
            var de = new DeserializerBuilder().Build();
            for (int i = 0; i < Iterations; i++)
            {
                var v = GenValue(4, allowNull: true);
                var yamlText = ser.Serialize(ToPlain(v));
                var parsed = de.Deserialize<object?>(new StringReader(yamlText));
                GcfLossless(FromYaml(parsed), $"yaml#{i}");
            }
        }

        // ---------- TOML ----------
        [Fact]
        public void Toml()
        {
            _rng = new Random(Seed ^ 0x2);
            for (int i = 0; i < Iterations; i++)
            {
                var v = GenTomlTable(3);
                string tomlText = Tomlyn.Toml.FromModel((TomlTable)ToToml(v)!);
                var model = Tomlyn.Toml.ToModel(tomlText);
                GcfLossless(FromToml(model), $"toml#{i}");
            }
        }

        // ---------- CSV ----------
        [Fact]
        public void Csv()
        {
            _rng = new Random(Seed ^ 0x3);
            for (int i = 0; i < Iterations; i++)
            {
                var records = GenCsvRecords();
                var canonical = CsvRoundTrip(records);
                GcfLossless(canonical, $"csv#{i}");
            }
        }

        // ===== generators =====
        private object? GenValue(int depth, bool allowNull)
        {
            if (depth <= 0 || _rng.NextDouble() < 0.4) return Scalar(allowNull);
            if (_rng.NextDouble() < 0.6)
            {
                var m = new OrderedMap();
                int n = _rng.Next(1, 5);
                for (int i = 0; i < n; i++) m[KeyPool[_rng.Next(KeyPool.Length)]] = GenValue(depth - 1, allowNull);
                return m;
            }
            int len = _rng.Next(0, 4);
            var l = new List<object?>();
            for (int i = 0; i < len; i++) l.Add(GenValue(depth - 1, allowNull));
            return l;
        }

        // TOML: top-level table, string keys, no null, values scalar/array-of-scalars/sub-table.
        private OrderedMap GenTomlTable(int depth)
        {
            var m = new OrderedMap();
            int n = _rng.Next(1, 5);
            var used = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                string k = TomlKey();
                if (!used.Add(k)) continue;
                double r = _rng.NextDouble();
                if (depth > 0 && r < 0.25) m[k] = GenTomlTable(depth - 1);
                else if (r < 0.45)
                {
                    // homogeneous scalar array (TOML 1.0 allows mixed, but keep it simple)
                    int len = _rng.Next(0, 4);
                    var arr = new List<object?>();
                    for (int j = 0; j < len; j++) arr.Add((long)_rng.Next(-100, 100));
                    m[k] = arr;
                }
                else m[k] = Scalar(allowNull: false);
            }
            return m;
        }
        private string TomlKey()
        {
            string[] k = { "id", "name", "a", "b", "x1", "key", "sub", "count", "dept" };
            return k[_rng.Next(k.Length)];
        }

        // CSV: uniform array of flat records, string keys, scalar values.
        private List<object?> GenCsvRecords()
        {
            int cols = _rng.Next(1, 5);
            var keys = new List<string>();
            var seen = new HashSet<string>();
            while (keys.Count < cols) { var k = KeyPool[_rng.Next(KeyPool.Length)]; if (seen.Add(k)) keys.Add(k); }
            int rows = _rng.Next(1, 6);
            var list = new List<object?>();
            for (int r = 0; r < rows; r++)
            {
                var m = new OrderedMap();
                foreach (var k in keys) m[k] = Scalar(allowNull: false);
                list.Add(m);
            }
            return list;
        }

        // ===== format bridges =====
        private static object? ToPlain(object? v)
        {
            switch (v)
            {
                case OrderedMap m:
                    var d = new Dictionary<string, object?>();
                    foreach (var kv in m) d[kv.Key] = ToPlain(kv.Value);
                    return d;
                case IList list when !(v is string):
                    return list.Cast<object?>().Select(ToPlain).ToList();
                default: return v;
            }
        }

        private static object? FromYaml(object? o)
        {
            if (o is IDictionary map && !(o is string))
            {
                var m = new OrderedMap();
                foreach (DictionaryEntry e in map) m[e.Key?.ToString() ?? ""] = FromYaml(e.Value);
                return m;
            }
            if (o is IList list && !(o is string))
                return list.Cast<object?>().Select(FromYaml).ToList();
            return o; // YamlDotNet returns strings for scalars, null for null
        }

        private static object? ToToml(object? v)
        {
            switch (v)
            {
                case OrderedMap m:
                    var t = new TomlTable();
                    foreach (var kv in m) t[kv.Key] = ToToml(kv.Value)!;
                    return t;
                case IList list when !(v is string):
                    var a = new TomlArray();
                    foreach (var it in list) a.Add(ToToml(it)!);
                    return a;
                default: return v;
            }
        }

        private static object? FromToml(object? o)
        {
            if (o is TomlTable tt)
            {
                var m = new OrderedMap();
                foreach (var kv in tt) m[kv.Key] = FromToml(kv.Value);
                return m;
            }
            if (o is TomlArray ta)
                return ta.Select(FromToml).ToList();
            if (o is int ii) return (long)ii;
            return o; // string, long, double, bool
        }

        private static object? CsvRoundTrip(List<object?> records)
        {
            var first = (OrderedMap)records[0]!;
            var keys = first.Keys.ToList();
            var sb = new StringBuilder();
            sb.Append(string.Join(",", keys.Select(CsvEscape))).Append('\n');
            foreach (OrderedMap rec in records)
                sb.Append(string.Join(",", keys.Select(k => CsvEscape(ScalarToString(rec.GetOrNull(k)))))).Append('\n');

            var lines = sb.ToString().TrimEnd('\n').Split('\n');
            var header = ParseCsvLine(lines[0]);
            var outList = new List<object?>();
            for (int i = 1; i < lines.Length; i++)
            {
                var cells = ParseCsvLine(lines[i]);
                var m = new OrderedMap();
                for (int j = 0; j < header.Count; j++) m[header[j]] = cells[j];
                outList.Add(m);
            }
            return outList;
        }

        private static string ScalarToString(object? v) => v switch
        {
            null => "",
            bool b => b ? "true" : "false",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString() ?? ""
        };

        private static string CsvEscape(string s)
            => (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private static List<string> ParseCsvLine(string line)
        {
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else inQ = false; }
                    else cur.Append(c);
                }
                else if (c == '"') inQ = true;
                else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        // ===== JSON helpers =====
        private static object? FromJsonElement(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var m = new OrderedMap();
                    foreach (var p in e.EnumerateObject()) m[p.Name] = FromJsonElement(p.Value);
                    return m;
                case JsonValueKind.Array:
                    return e.EnumerateArray().Select(FromJsonElement).ToList<object?>();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number:
                    var raw = e.GetRawText();
                    if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0 && e.TryGetInt64(out var lv)) return lv;
                    return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        private static string NativeToJson(object? v)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream)) WriteNative(w, v);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        private static void WriteNative(Utf8JsonWriter w, object? v)
        {
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case long l: w.WriteNumberValue(l); break;
                case int i: w.WriteNumberValue(i); break;
                case double d: w.WriteNumberValue(d); break;
                case string s: w.WriteStringValue(s); break;
                case OrderedMap m:
                    w.WriteStartObject();
                    foreach (var kv in m) { w.WritePropertyName(kv.Key); WriteNative(w, kv.Value); }
                    w.WriteEndObject();
                    break;
                case IList list:
                    w.WriteStartArray();
                    foreach (var it in list) WriteNative(w, it);
                    w.WriteEndArray();
                    break;
                default: w.WriteStringValue(v.ToString()); break;
            }
        }
    }
}
