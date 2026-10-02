using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    /// <summary>
    /// Targeted fuzz/property coverage for spec v3.6.0 (constant-column factoring §7.4.7 and
    /// value-grouping §7.4.8), mirroring gcf-go/constant_grouping_fuzz_test.go. The default
    /// round-trips already exercise constant-factoring because it rides the default encoder,
    /// but value-grouping is opt-in (EncodeGenericGrouped) and neither new grammar had a
    /// decoder-robustness (mutation) fuzz. These harnesses close both gaps.
    ///
    /// Per-SDK fuzz is required: the .NET SDK has a history of edge-case bugs only a native
    /// fuzz caught. Iterations/seed are env-configurable (GCF_FUZZ_N, GCF_FUZZ_SEED).
    /// </summary>
    public class ConstantGroupingFuzzTests
    {
        private static int Iterations =>
            int.TryParse(Environment.GetEnvironmentVariable("GCF_FUZZ_N"), out var n) && n > 0 ? n : 20000;

        // hazardStrings mimic v3.6.0 syntax tokens (clauses, subheaders, structural markers)
        // so the generators can plant them as field names and values. A value or name that
        // LOOKS like a grouping clause, a constant entry, a subheader, or another shape's
        // marker must still decode as plain data, never reclassify the payload.
        private static readonly string[] HazardStrings =
        {
            "group=dept", "group=", "region=us-east", "= [1]", "k=v [1]",
            "dept=Sales [2]", "}", "{a}", "[2]", "[2:]", "[0]", "[?]",
            "## section", ".field", "@id", "@0", "a|b", "-", "~",
            "^", "^{abc", "^{a}", "^{", "^x", "^{a,b}",
        };

        // adversarial scalar pool: commas, braces, quotes, leading/trailing space,
        // numeric-like, markers, unicode.
        private static readonly object?[] AdversarialScalars =
        {
            "a,b", "x}y", "a\"b", " lead", "trail ", "-", "~", "^", "5", "3.14",
            "0x", "01", "true", "false", "", "plain", "café", "a|b", "=v", "#x",
            0L, 1L, -1L, 42L, 9223372036854775807L, 1.5, true, false, null,
        };

        private object? GenAdversarialScalar(Random rng) => AdversarialScalars[rng.Next(AdversarialScalars.Length)];

        private object? GenScalar(Random rng)
        {
            switch (rng.Next(6))
            {
                case 0: return rng.Next(-1000, 1000) is var i ? (long)i : 0L;
                case 1: return (rng.NextDouble() - 0.5) * 100.0;
                case 2: return rng.Next(2) == 0;
                case 3: return null;
                case 4: return GenAdversarialScalar(rng);
                default: return "s" + rng.Next(0, 50).ToString(CultureInfo.InvariantCulture);
            }
        }

        private object? HazardValue(Random rng) =>
            rng.Next(3) == 0 ? HazardStrings[rng.Next(HazardStrings.Length)] : GenAdversarialScalar(rng);

        private static readonly string[] AdversarialKeys =
        {
            "", ">", "a>b", "a|b", "a,b", "a=b", "@x", "#x", "5", "true", "a b", "x\n", "\"q\"",
        };

        private string GenKey(Random rng) => AdversarialKeys[rng.Next(AdversarialKeys.Length)];
        private string GenBareKey(Random rng) => "f" + rng.Next(0, 1000).ToString(CultureInfo.InvariantCulture);

        // genFieldName returns mostly bare keys, sometimes a quoting-required key (including
        // names that contain "=", which must NOT be read as a constant-column separator, and
        // names that mimic other markers). Never returns a name already used.
        private string GenFieldName(Random rng, HashSet<string> used)
        {
            while (true)
            {
                string f;
                switch (rng.Next(4))
                {
                    case 0: f = GenKey(rng); break;
                    case 1: f = HazardStrings[rng.Next(HazardStrings.Length)]; break;
                    default: f = GenBareKey(rng); break;
                }
                if (used.Add(f)) return f;
            }
        }

        private static object? RecField(object? rec, string name)
        {
            if (rec is OrderedMap m) { m.TryGetValue(name, out var v); return v; }
            return null;
        }

        // --- constant-column factoring: constant-biased generator ---

        private List<object?> GenConstBiasedArray(Random rng)
        {
            int n = 2 + rng.Next(6); // 2..7 records
            int k = 1 + rng.Next(5); // 1..5 fields
            var fields = new List<string>();
            var used = new HashSet<string>();
            while (fields.Count < k) fields.Add(GenFieldName(rng, used));
            var constVal = new Dictionary<string, object?>();
            bool forceAll = rng.Next(8) == 0; // ~12% all-constant
            foreach (var f in fields)
                if (forceAll || rng.Next(2) == 0) constVal[f] = HazardValue(rng);
            var arr = new List<object?>(n);
            for (int i = 0; i < n; i++)
            {
                var rec = new OrderedMap();
                foreach (var f in fields)
                {
                    if (constVal.TryGetValue(f, out var cv)) rec[f] = cv;
                    else if (rng.Next(4) == 0) rec[f] = HazardValue(rng);
                    else rec[f] = GenScalar(rng);
                }
                arr.Add(rec);
            }
            return arr;
        }

        [Fact]
        public void PropertyRoundTripConstantBiased()
        {
            var rng = new Random(0xC0);
            int factored = 0;
            for (int i = 0; i < Iterations; i++)
            {
                var val = GenConstBiasedArray(rng);
                string gcfText = Gcf.EncodeGeneric(val);
                if (HeaderHasFactoredColumn(gcfText)) factored++;
                object? decoded;
                try { decoded = Gcf.DecodeGeneric(gcfText); }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"iter {i}: decode failed: {ex.Message}\n  gcf: {Trunc(gcfText, 500)}");
                }
                if (!GenericConformanceTests.DeepEqual(val, decoded))
                    throw new Xunit.Sdk.XunitException($"iter {i}: round-trip mismatch\n  gcf: {Trunc(gcfText, 500)}");
            }
            if (factored == 0)
                throw new Xunit.Sdk.XunitException($"coverage gap: no factored headers produced in {Iterations} iterations");
        }

        private static bool HeaderHasFactoredColumn(string gcf)
        {
            foreach (var line in gcf.Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    int open = line.IndexOf('{');
                    int close = line.LastIndexOf('}');
                    if (open >= 0 && close > open && line.Substring(open, close - open).IndexOf('=') >= 0) return true;
                }
            }
            return false;
        }

        // --- value-grouping: keyed-set generator ---

        private (List<object?> arr, string keyField, string groupField) GenGroupedSet(Random rng)
        {
            string keyField = "k", groupField = "g";
            int n = 2 + rng.Next(8); // 2..9 records
            int poolSize = 1 + rng.Next(4);
            var pool = new List<object?>();
            var seen = new HashSet<string>();
            while (pool.Count < poolSize)
            {
                var v = HazardValue(rng);
                string kkey = (v?.GetType().FullName ?? "null") + "/" + StableStr(v);
                if (!seen.Add(kkey)) continue;
                pool.Add(v);
            }
            int extraN = rng.Next(4);
            var extras = new List<string>();
            var used = new HashSet<string> { "k", "g" };
            while (extras.Count < extraN) extras.Add(GenFieldName(rng, used));
            var extraConst = new Dictionary<string, object?>();
            foreach (var f in extras)
                if (rng.Next(2) == 0) extraConst[f] = HazardValue(rng);
            var arr = new List<object?>(n);
            for (int i = 0; i < n; i++)
            {
                var rec = new OrderedMap();
                rec[keyField] = "k" + i.ToString("D4", CultureInfo.InvariantCulture);
                rec[groupField] = pool[rng.Next(pool.Count)];
                foreach (var f in extras)
                {
                    if (extraConst.TryGetValue(f, out var cv)) rec[f] = cv;
                    else if (rng.Next(4) == 0) rec[f] = HazardValue(rng);
                    else rec[f] = GenScalar(rng);
                }
                arr.Add(rec);
            }
            return (arr, keyField, groupField);
        }

        private static void SortByKey(List<object?> arr, string keyField)
        {
            arr.Sort((a, b) => string.CompareOrdinal(
                FormatForSort(RecField(a, keyField)), FormatForSort(RecField(b, keyField))));
        }

        private static string FormatForSort(object? v) => StableStr(v);

        // A stable, culture-invariant string for a scalar, used only for sort/dedup keys in
        // the fuzz harness (not wire output), so it needs no access to the library internals.
        private static string StableStr(object? v)
        {
            switch (v)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return s;
                default: return v.ToString() ?? "";
            }
        }

        [Fact]
        public void PropertyRoundTripGrouped()
        {
            var rng = new Random(0x6C);
            for (int i = 0; i < Iterations; i++)
            {
                var (val, kf, gf) = GenGroupedSet(rng);
                string gcfText;
                try { gcfText = Gcf.EncodeGenericGrouped(val, kf, gf); }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"iter {i}: EncodeGenericGrouped failed on valid keyed set: {ex.Message}");
                }
                object? decodedAny;
                try { decodedAny = Gcf.DecodeGeneric(gcfText); }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"iter {i}: decode failed: {ex.Message}\n  gcf: {Trunc(gcfText, 500)}");
                }
                if (!(decodedAny is IList decoded) || decodedAny is string)
                    throw new Xunit.Sdk.XunitException($"iter {i}: grouped decode did not yield an array: {decodedAny?.GetType()}");
                if (decoded.Count != val.Count)
                    throw new Xunit.Sdk.XunitException($"iter {i}: record count {decoded.Count} != {val.Count}\n  gcf: {Trunc(gcfText, 500)}");
                // compare as a set keyed by kf: sort both by key, then order-insensitive deep-equal.
                var inCopy = new List<object?>(val);
                var decCopy = decoded.Cast<object?>().ToList();
                SortByKey(inCopy, kf);
                SortByKey(decCopy, kf);
                if (!GenericConformanceTests.DeepEqual(inCopy, decCopy))
                    throw new Xunit.Sdk.XunitException($"iter {i}: grouped round-trip mismatch\n  gcf: {Trunc(gcfText, 500)}");
            }
        }

        // --- decoder robustness (mutation) ---

        private static readonly byte[] StructuralBytes = System.Text.Encoding.ASCII.GetBytes("|{}[]=@#.-~\"\n ");

        private byte[] Mutate(Random rng, byte[] b)
        {
            if (b.Length == 0) return new[] { (byte)rng.Next(128) };
            var list = new List<byte>(b);
            switch (rng.Next(6))
            {
                case 0: { int i = rng.Next(list.Count); list[i] ^= (byte)(1 << rng.Next(8)); break; }
                case 1: { int i = rng.Next(list.Count); list.RemoveAt(i); break; }
                case 2: { int i = rng.Next(list.Count + 1); list.Insert(i, StructuralBytes[rng.Next(StructuralBytes.Length)]); break; }
                case 3: { list = list.Take(rng.Next(list.Count)).ToList(); break; }
                case 4: { int i = rng.Next(list.Count); var frag = list.Skip(i).ToList(); list.InsertRange(i, frag); break; }
                default: { int i = rng.Next(list.Count); list[i] = (byte)rng.Next(128); break; }
            }
            return list.ToArray();
        }

        // Mutates valid factored/grouped wire and requires the decoder to error cleanly,
        // never throw a non-DecodeException (parser bug) on the result.
        [Fact]
        public void ConstantGroupedDecodeRobustness()
        {
            var rng = new Random(0xF0);
            for (int i = 0; i < Iterations; i++)
            {
                string wire;
                if (rng.Next(2) == 0)
                {
                    wire = Gcf.EncodeGeneric(GenConstBiasedArray(rng));
                }
                else
                {
                    var (arr, kf, gf) = GenGroupedSet(rng);
                    try { wire = Gcf.EncodeGenericGrouped(arr, kf, gf); }
                    catch { continue; }
                }
                var b = System.Text.Encoding.UTF8.GetBytes(wire);
                for (int m = 1 + rng.Next(4); m > 0; m--) b = Mutate(rng, b);
                // An error is fine; a crash (anything other than a thrown GCF exception) is not.
                try { Gcf.DecodeGeneric(b); }
                catch (DecodeException) { }
                catch (EncodeException) { }
                catch (ArgumentException) { }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"iter {i}: unexpected exception type {ex.GetType().Name} on mutated input: {ex.Message}\n  input: {Trunc(SafeStr(b), 500)}");
                }
            }
        }

        // Pins that the v3.6.0 markers do not reclassify a payload of another shape:
        // an @-marked field without a group= clause stays invalid (not silently grouped),
        // a keyed map stays a map (not read as grouped), and a flat tabular array stays flat.
        [Fact]
        public void ShapeDiscrimination()
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                Gcf.DecodeGeneric("GCF profile=generic\n## [2]{@id,x}\nu1|1\nu2|2\n"));
            Assert.Contains("invalid field name", ex.Message);

            var got = Gcf.DecodeGeneric("GCF profile=generic\n## [2:]{key,x}\na|1\nb|2\n");
            Assert.IsType<OrderedMap>(got);

            var flat = new List<object?>
            {
                MakeRec(("id", "u1"), ("r", "a")),
                MakeRec(("id", "u2"), ("r", "b")),
            };
            string wire = Gcf.EncodeGeneric(flat);
            Assert.False(HeaderHasFactoredColumn(wire), "flat array with varying columns should not factor: " + wire);
            var dec = Gcf.DecodeGeneric(wire);
            Assert.True(GenericConformanceTests.DeepEqual(flat, dec), "flat round-trip failed: " + wire);
        }

        private static OrderedMap MakeRec(params (string k, object? v)[] kvs)
        {
            var m = new OrderedMap();
            foreach (var (k, v) in kvs) m[k] = v;
            return m;
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
        private static string SafeStr(byte[] b)
        {
            try { return System.Text.Encoding.UTF8.GetString(b); }
            catch { return BitConverter.ToString(b); }
        }
    }
}
