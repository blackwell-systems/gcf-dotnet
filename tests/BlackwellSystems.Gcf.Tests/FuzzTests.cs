using System;
using System.Collections.Generic;
using System.Linq;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    /// <summary>
    /// Property-based fuzz over the generic codec. Generates random NATIVE structured
    /// values (nested OrderedMap / IList / scalars, not JSON) from an adversarial
    /// alphabet, and asserts the codec's core invariants:
    ///   1. decode does not throw on any encoder output, and re-encoding the decoded
    ///      value is a fixed point (encode == encode(decode(encode(v)))). This is the
    ///      "decode faithfully inverts encode up to canonical form" property, matching
    ///      the ecosystem's cross-SDK differential fuzz (idempotent decode).
    ///   2. generic delta round-trips: diff -> encode -> decode -> verify reconstructs
    ///      the next set's content-addressed pack root.
    ///
    /// Iterations and seed are env-configurable (GCF_FUZZ_N, GCF_FUZZ_SEED) so CI can
    /// crank the count; the default is a quick self-check.
    /// </summary>
    public class FuzzTests
    {
        private static int Iterations =>
            int.TryParse(Environment.GetEnvironmentVariable("GCF_FUZZ_N"), out var n) && n > 0 ? n : 3000;
        private static int Seed =>
            int.TryParse(Environment.GetEnvironmentVariable("GCF_FUZZ_SEED"), out var s) ? s : 1;

        // Adversarial alphabets (mirrors scripts/differential-fuzz.py): structural
        // delimiters, empty / `>` / quote / comma keys, grapheme-extending scalars.
        private static readonly string[] Keys =
        {
            "", ">", ">>", "a>b", "a>>b", ">b", "a|b", "a,b", "a=b", "@x", "#x", "5",
            "true", "key", "a b", "café", "x\n", "\"q\"", "a\\b", "id", "name", "ௗid", "aௗb"
        };
        private static readonly object?[] Scalars =
        {
            true, false, null, 0L, -1L, 42L, 3.14, -0.0, 1e18, "5", "true", "-", "~", "^",
            "@x", "a|b", "a,b", "café", "x\n", "", "^{a}", "plain", "=v", " ૌx", "ௗv"
        };

        private Random _rng = new Random(Seed);
        private string GKey() => Keys[_rng.Next(Keys.Length)];
        private object? GScalar() => Scalars[_rng.Next(Scalars.Length)];

        private object? GVal(int depth)
        {
            if (depth <= 0 || _rng.NextDouble() < 0.35) return GScalar();
            double r = _rng.NextDouble();
            if (r < 0.6)
            {
                var o = new OrderedMap();
                int n = _rng.Next(0, 5);
                for (int i = 0; i < n; i++) o[GKey()] = GVal(depth - 1); // later dup key overwrites, like a map
                return o;
            }
            if (r < 0.85)
            {
                int n = _rng.Next(0, 4);
                var l = new List<object?>();
                for (int i = 0; i < n; i++) l.Add(GVal(depth - 1));
                return l;
            }
            return GScalar();
        }

        [Fact]
        public void GenericCodec_DecodeIsIdempotentFixedPoint()
        {
            _rng = new Random(Seed);
            for (int i = 0; i < Iterations; i++)
            {
                var v = GVal(4);
                string wire1 = Gcf.EncodeGeneric(v);
                object? decoded;
                try { decoded = Gcf.DecodeGeneric(wire1); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"decode threw at iter {i}: {ex.Message}\nwire:\n{wire1}"); }
                string wire2 = Gcf.EncodeGeneric(decoded);
                if (wire1 != wire2)
                    throw new Xunit.Sdk.XunitException($"non-idempotent at iter {i}\n--- wire1 ---\n{wire1}\n--- wire2 ---\n{wire2}");
            }
        }

        [Fact]
        public void GenericDelta_RoundTripsAndReconciles()
        {
            _rng = new Random(Seed ^ 0x5eed);
            var fields = new[] { "id", "a", "b" };
            for (int i = 0; i < Iterations; i++)
            {
                var baseSet = RandomSet(fields, _rng.Next(0, 8));
                var next = Mutate(baseSet, fields);

                var d = Gcf.DiffGenericSets(baseSet, next);
                string wire = Gcf.EncodeGenericDelta(d);
                var d2 = Gcf.DecodeGenericDelta(wire);
                GenericSet applied;
                try { applied = Gcf.VerifyGenericDelta(baseSet, d2, d2.NewRoot); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"delta verify threw at iter {i}: {ex.Message}\nwire:\n{wire}"); }

                if (Gcf.GenericPackRoot(applied) != Gcf.GenericPackRoot(next))
                    throw new Xunit.Sdk.XunitException($"delta did not reconcile at iter {i}\nwire:\n{wire}");
            }
        }

        private GenericSet RandomSet(string[] fields, int count)
        {
            var rows = new List<OrderedMap>();
            var used = new HashSet<long>();
            for (int i = 0; i < count; i++)
            {
                long id;
                do { id = _rng.Next(0, 20); } while (!used.Add(id)); // unique identity
                var row = new OrderedMap { ["id"] = id, ["a"] = DeltaScalar(), ["b"] = DeltaScalar() };
                rows.Add(row);
            }
            return new GenericSet("id", fields, rows, "rows");
        }

        // Delta rows carry flat scalars only.
        private object? DeltaScalar()
        {
            object?[] s = { true, false, null, 0L, 42L, -7L, 3.14, "shipped", "pending", "a|b", "café", "", "-" };
            return s[_rng.Next(s.Length)];
        }

        private GenericSet Mutate(GenericSet baseSet, string[] fields)
        {
            var rows = new List<OrderedMap>();
            var used = new HashSet<long>();
            foreach (var r in baseSet.Rows)
            {
                double act = _rng.NextDouble();
                if (act < 0.3) continue; // remove
                long id = (long)r.GetOrNull("id")!;
                used.Add(id);
                if (act < 0.6) // change
                    rows.Add(new OrderedMap { ["id"] = id, ["a"] = DeltaScalar(), ["b"] = DeltaScalar() });
                else // keep
                    rows.Add(new OrderedMap { ["id"] = id, ["a"] = r.GetOrNull("a"), ["b"] = r.GetOrNull("b") });
            }
            int adds = _rng.Next(0, 3);
            for (int i = 0; i < adds; i++)
            {
                long id;
                int guard = 0;
                do { id = _rng.Next(0, 40); } while (!used.Add(id) && ++guard < 50);
                rows.Add(new OrderedMap { ["id"] = id, ["a"] = DeltaScalar(), ["b"] = DeltaScalar() });
            }
            return new GenericSet("id", fields, rows, "rows");
        }
    }
}
