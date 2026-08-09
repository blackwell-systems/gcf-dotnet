using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    public class GraphConformanceTests
    {
        private static readonly string[] Categories =
        {
            "graph-encode", "graph-decode", "graph-pack-root", "graph-session", "graph-delta", "streaming-v2"
        };

        public static IEnumerable<object[]> Fixtures()
        {
            var root = GenericConformanceTests.ConformanceDir();
            foreach (var cat in Categories)
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
            var root = GenericConformanceTests.ConformanceDir();
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            var op = r.GetProperty("operation").GetString();

            switch (op)
            {
                case "encode":
                    Assert.Equal(r.GetProperty("expected").GetString(), Gcf.Encode(PayloadFromJson(r.GetProperty("input"))));
                    break;

                case "decode":
                    GenericConformanceTests.AssertDeepEqual(
                        NormalizePayload(ConformanceInput.FromJson(r.GetProperty("expected"))),
                        NormalizePayload(Gcf.DecodeGeneric(r.GetProperty("input").GetString()!)), relative);
                    break;

                case "pack-root":
                    {
                        var input = r.GetProperty("input");
                        var syms = SymbolsFromJson(input.GetProperty("symbols"));
                        var edges = EdgesFromJson(input.GetProperty("edges"));
                        Assert.Equal(r.GetProperty("expected").GetString(), Gcf.PackRoot(syms, edges));
                        break;
                    }

                case "session":
                    // Placeholder fixtures with null input/expected; nothing to assert.
                    break;

                case "delta":
                    {
                        var input = r.GetProperty("input");
                        if (input.ValueKind == JsonValueKind.String)
                        {
                            // decode a delta wire, apply to base_snapshot, verify new_root.
                            var d = Gcf.DecodeDelta(input.GetString()!);
                            var baseSnap = r.GetProperty("base_snapshot");
                            var newRoot = r.GetProperty("new_root").GetString()!;
                            Gcf.VerifyDelta(SymbolsFromJson(baseSnap.GetProperty("symbols")), EdgesFromJson(baseSnap.GetProperty("edges")),
                                d.Removed, d.Added, d.RemovedEdges, d.AddedEdges, newRoot);
                        }
                        else
                        {
                            Assert.Equal(r.GetProperty("expected").GetString(), Gcf.EncodeDelta(DeltaFromJson(input)));
                        }
                        break;
                    }

                case "delta-verify":
                    {
                        var wire = r.GetProperty("input").GetString()!;
                        var baseSnap = r.GetProperty("base_snapshot");
                        var baseSyms = SymbolsFromJson(baseSnap.GetProperty("symbols"));
                        var baseEdges = EdgesFromJson(baseSnap.GetProperty("edges"));
                        var d = Gcf.DecodeDelta(wire);
                        if (r.TryGetProperty("expectedError", out _))
                        {
                            Assert.ThrowsAny<Exception>(() =>
                                Gcf.VerifyDelta(baseSyms, baseEdges, d.Removed, d.Added, d.RemovedEdges, d.AddedEdges, d.NewRoot));
                        }
                        else
                        {
                            Gcf.VerifyDelta(baseSyms, baseEdges, d.Removed, d.Added, d.RemovedEdges, d.AddedEdges, d.NewRoot);
                        }
                        break;
                    }

                case "graph-stream-encode":
                    {
                        var input = r.GetProperty("input");
                        var payload = PayloadFromJson(input);
                        bool labeled = false, session = false;
                        if (r.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Object)
                        {
                            labeled = optEl.TryGetProperty("labeledTrailerCounts", out var lt) && lt.ValueKind == JsonValueKind.True;
                            session = optEl.TryGetProperty("session", out var se) && se.ValueKind == JsonValueKind.True;
                        }
                        var opts = new StreamOptions
                        {
                            TokenBudget = payload.TokenBudget,
                            TokensUsed = payload.TokensUsed,
                            PackRoot = payload.PackRoot,
                            Session = session,
                            LabeledTrailerCounts = labeled,
                        };
                        var sw = new StringWriter { NewLine = "\n" };
                        var enc = new StreamEncoder(sw, payload.Tool, opts);
                        foreach (var s in payload.Symbols) enc.WriteSymbol(s);
                        foreach (var e in payload.Edges) enc.WriteEdge(e);
                        enc.Close();
                        Assert.Equal(r.GetProperty("expected").GetString(), sw.ToString());
                        break;
                    }

                default:
                    throw new Xunit.Sdk.XunitException("unknown graph operation: " + op);
            }
        }

        // Graph-decode fixtures are inconsistent about whether zero-valued header
        // fields are present; normalize by dropping tokenBudget/tokensUsed when 0 and
        // packRoot when empty on both sides before comparing.
        private static object? NormalizePayload(object? v)
        {
            if (!(v is OrderedMap m)) return v;
            if (!m.ContainsKey("symbols")) return v; // not a payload map
            var outM = new OrderedMap();
            foreach (var kv in m)
            {
                if ((kv.Key == "tokenBudget" || kv.Key == "tokensUsed") && kv.Value is long l && l == 0) continue;
                if (kv.Key == "packRoot" && kv.Value is string s && s.Length == 0) continue;
                outM[kv.Key] = kv.Value;
            }
            return outM;
        }

        private static Payload PayloadFromJson(JsonElement e) => new Payload
        {
            Tool = e.TryGetProperty("tool", out var t) ? t.GetString()! : "",
            TokenBudget = e.TryGetProperty("tokenBudget", out var tb) ? tb.GetInt32() : 0,
            TokensUsed = e.TryGetProperty("tokensUsed", out var tu) ? tu.GetInt32() : 0,
            PackRoot = e.TryGetProperty("packRoot", out var pr) ? pr.GetString()! : "",
            Symbols = e.TryGetProperty("symbols", out var sy) ? SymbolsFromJson(sy) : new List<Symbol>(),
            Edges = e.TryGetProperty("edges", out var ed) ? EdgesFromJson(ed) : new List<Edge>(),
        };

        private static List<Symbol> SymbolsFromJson(JsonElement a) => a.EnumerateArray().Select(s => new Symbol
        {
            QualifiedName = s.GetProperty("qualifiedName").GetString()!,
            Kind = s.GetProperty("kind").GetString()!,
            Score = s.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0,
            Provenance = s.TryGetProperty("provenance", out var pv) ? pv.GetString()! : "",
            Distance = s.TryGetProperty("distance", out var ds) ? ds.GetInt32() : 0,
            Signature = s.TryGetProperty("signature", out var sg) ? sg.GetString()! : "",
        }).ToList();

        private static List<Edge> EdgesFromJson(JsonElement a) => a.EnumerateArray().Select(e => new Edge
        {
            Source = e.GetProperty("source").GetString()!,
            Target = e.GetProperty("target").GetString()!,
            EdgeType = e.GetProperty("edgeType").GetString()!,
            Status = e.TryGetProperty("status", out var st) ? st.GetString()! : "",
        }).ToList();

        private static DeltaPayload DeltaFromJson(JsonElement e) => new DeltaPayload
        {
            Tool = e.TryGetProperty("tool", out var t) ? t.GetString()! : "",
            BaseRoot = e.TryGetProperty("baseRoot", out var br) ? br.GetString()! : "",
            NewRoot = e.TryGetProperty("newRoot", out var nr) ? nr.GetString()! : "",
            Removed = e.TryGetProperty("removed", out var rm) ? SymbolsFromJson(rm) : new List<Symbol>(),
            Added = e.TryGetProperty("added", out var ad) ? SymbolsFromJson(ad) : new List<Symbol>(),
            RemovedEdges = e.TryGetProperty("removedEdges", out var re) ? EdgesFromJson(re) : new List<Edge>(),
            AddedEdges = e.TryGetProperty("addedEdges", out var ae) ? EdgesFromJson(ae) : new List<Edge>(),
            DeltaTokens = e.TryGetProperty("deltaTokens", out var dt) ? dt.GetInt32() : 0,
            FullTokens = e.TryGetProperty("fullTokens", out var ft) ? ft.GetInt32() : 0,
        };
    }
}
