using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    public class GenericConformanceTests
    {
        // Categories exercising the generic profile (graph/delta/streaming ported later).
        private static readonly string[] GenericCategories =
        {
            "scalar", "numbers", "keys", "whitespace", "arrays", "containers",
            "roots", "flatten", "attachments", "inline-schema", "keyed-map",
            "decode", "errors-v2"
        };

        public static string ConformanceDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                var cand = Path.Combine(d.FullName, "gcf", "tests", "conformance");
                if (Directory.Exists(cand)) return cand;
                d = d.Parent;
            }
            throw new DirectoryNotFoundException("conformance fixtures not found (expected a sibling gcf/tests/conformance)");
        }

        public static IEnumerable<object[]> Fixtures()
        {
            var root = ConformanceDir();
            foreach (var cat in GenericCategories)
            {
                var dir = Path.Combine(root, cat);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f))
                    yield return new object[] { cat + "/" + Path.GetFileName(file) };
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Fixture(string relative)
        {
            var root = ConformanceDir();
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            var op = r.GetProperty("operation").GetString();

            switch (op)
            {
                case "encode":
                    {
                        var input = ConformanceInput.FromJson(r.GetProperty("input"));
                        var expected = r.GetProperty("expected").GetString();
                        Assert.Equal(expected, Gcf.EncodeGeneric(input, ReadOptions(r)));
                        break;
                    }
                case "decode":
                    {
                        var wire = r.GetProperty("input").GetString()!;
                        var expected = ConformanceInput.FromJson(r.GetProperty("expected"));
                        AssertDeepEqual(expected, Gcf.DecodeGeneric(wire), relative);
                        break;
                    }
                case "roundtrip":
                    {
                        // input is a value; expected is the canonical GCF wire. Encode must
                        // match the wire, and decoding the wire must reproduce the input.
                        var input = ConformanceInput.FromJson(r.GetProperty("input"));
                        var expectedWire = r.GetProperty("expected").GetString();
                        var wire = Gcf.EncodeGeneric(input, ReadOptions(r));
                        Assert.Equal(expectedWire, wire);
                        AssertDeepEqual(input, Gcf.DecodeGeneric(wire), relative);
                        break;
                    }
                case "error":
                    {
                        if (r.TryGetProperty("input", out var inp))
                        {
                            var wire = inp.GetString()!;
                            Assert.ThrowsAny<Exception>(() => Gcf.DecodeGeneric(wire));
                        }
                        else
                        {
                            // Byte-level input (e.g. invalid UTF-8). .NET strings are UTF-16, so
                            // the transport decodes bytes with a strict UTF-8 decoder that rejects
                            // invalid sequences (the spec's invalid_utf8 error) before the codec.
                            var bytes = Convert.FromBase64String(r.GetProperty("inputBase64").GetString()!);
                            var strict = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);
                            Assert.ThrowsAny<Exception>(() => Gcf.DecodeGeneric(strict.GetString(bytes)));
                        }
                        break;
                    }
                default:
                    throw new Xunit.Sdk.XunitException("unknown operation: " + op);
            }
        }

        private static GenericOptions ReadOptions(JsonElement r)
        {
            var opts = new GenericOptions();
            if (r.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty("noFlatten", out var nf) && nf.ValueKind == JsonValueKind.True)
                opts.NoFlatten = true;
            return opts;
        }

        private static void AssertDeepEqual(object? expected, object? actual, string ctx)
        {
            if (!DeepEqual(expected, actual))
                throw new Xunit.Sdk.XunitException($"[{ctx}] decode mismatch\n  expected: {Render(expected)}\n  actual:   {Render(actual)}");
        }

        private static bool DeepEqual(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is OrderedMap ma && b is OrderedMap mb)
            {
                if (ma.Count != mb.Count) return false;
                foreach (var kv in ma)
                {
                    if (!mb.TryGetValue(kv.Key, out var bv)) return false;
                    if (!DeepEqual(kv.Value, bv)) return false;
                }
                return true;
            }
            if (a is IList la && !(a is string) && b is IList lb && !(b is string))
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!DeepEqual(la[i], lb[i])) return false;
                return true;
            }
            if (IsNum(a) && IsNum(b))
            {
                if (a is long al && b is long bl) return al == bl;
                return Convert.ToDouble(a) == Convert.ToDouble(b);
            }
            if (a is bool || b is bool) return a is bool && b is bool && (bool)a == (bool)b;
            return a.Equals(b);
        }

        private static bool IsNum(object? v) => v is long || v is double || v is int;

        private static string Render(object? v)
        {
            if (v == null) return "null";
            if (v is OrderedMap m) return "{" + string.Join(", ", m.Select(kv => kv.Key + ":" + Render(kv.Value))) + "}";
            if (v is IList l && !(v is string)) return "[" + string.Join(", ", l.Cast<object?>().Select(Render)) + "]";
            if (v is string s) return "\"" + s + "\"";
            return v.ToString() ?? "null";
        }
    }
}
