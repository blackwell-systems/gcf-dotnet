using System.Collections.Generic;
using System.IO;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    public class UnitTests
    {
        private static OrderedMap Map(params (string, object?)[] kvs)
        {
            var m = new OrderedMap();
            foreach (var (k, v) in kvs) m[k] = v;
            return m;
        }

        [Fact]
        public void GenericRoundTrip_VariousShapes()
        {
            var values = new object?[]
            {
                Map(("name", "Alice"), ("age", 30L), ("active", true), ("score", 9.5), ("note", (object?)null)),
                new List<object?> { Map(("id", 1L), ("t", "a")), Map(("id", 2L), ("t", "b")) },
                Map(("nested", Map(("a", 1L), ("b", new List<object?> { 1L, 2L, 3L })))),
                "hello world",
                42L,
                new List<object?> { "x", "y|z", "a,b" },
            };
            foreach (var v in values)
            {
                var wire = Gcf.EncodeGeneric(v);
                var back = Gcf.DecodeGeneric(wire);
                Assert.True(GenericConformanceTests.DeepEqual(v, back), "round trip failed for wire:\n" + wire);
            }
        }

        [Fact]
        public void GenericDelta_SessionReconstructsState()
        {
            GenericSet Set(params (long id, string status)[] rows)
            {
                var list = new List<OrderedMap>();
                foreach (var (id, status) in rows) list.Add(Map(("id", id), ("status", status)));
                return new GenericSet("id", new[] { "id", "status" }, list, "orders");
            }

            var b0 = Set((1, "new"), (2, "new"), (3, "new"));
            var session = new GenericDeltaSession(b0, "orders", ReanchorPolicy.SizeGuard());

            // Consumer starts in sync with the bootstrap full.
            var (held, _) = Gcf.DecodeGenericFull(session.CurrentFull());

            var updates = new[]
            {
                Set((1, "shipped"), (2, "new"), (3, "new")),
                Set((1, "shipped"), (2, "shipped"), (4, "new")),
            };
            foreach (var up in updates)
            {
                var (wire, isFull) = session.Next(up);
                if (isFull) { (held, _) = Gcf.DecodeGenericFull(wire); }
                else
                {
                    var d = Gcf.DecodeGenericDelta(wire);
                    held = Gcf.VerifyGenericDelta(held, d, d.NewRoot);
                }
                Assert.Equal(Gcf.GenericPackRoot(up), Gcf.GenericPackRoot(held));
            }
        }

        [Fact]
        public void GraphSession_SecondCallEmitsBareReferences()
        {
            var payload = new Payload
            {
                Tool = "ctx",
                Symbols = new List<Symbol>
                {
                    new Symbol { QualifiedName = "pkg.A", Kind = "function", Score = 0.9, Provenance = "lsp", Distance = 0 },
                    new Symbol { QualifiedName = "pkg.B", Kind = "function", Score = 0.5, Provenance = "lsp", Distance = 0 },
                },
            };
            var session = new Session();
            var first = Gcf.EncodeWithSession(payload, session);
            Assert.Contains("pkg.A", first);
            Assert.DoesNotContain("# previously transmitted", first);

            var second = Gcf.EncodeWithSession(payload, session);
            Assert.Contains("# previously transmitted", second);
            Assert.Equal(2, session.Size());
        }

        [Fact]
        public void GraphRoundTrip_PreservesSymbolsAndEdges()
        {
            var payload = new Payload
            {
                Tool = "ctx", TokenBudget = 5000, TokensUsed = 1200,
                Symbols = new List<Symbol>
                {
                    new Symbol { QualifiedName = "pkg.Handler", Kind = "function", Score = 0.95, Provenance = "lsp", Distance = 0 },
                    new Symbol { QualifiedName = "pkg.Store", Kind = "type", Score = 0.6, Provenance = "ast", Distance = 1 },
                },
                Edges = new List<Edge>
                {
                    new Edge { Source = "pkg.Handler", Target = "pkg.Store", EdgeType = "calls" },
                },
            };
            var wire = Gcf.Encode(payload);
            var back = Gcf.Decode(wire);
            Assert.Equal(2, back.Symbols.Count);
            Assert.Single(back.Edges);
            Assert.Equal("pkg.Handler", back.Edges[0].Source);
            Assert.Equal("pkg.Store", back.Edges[0].Target);
        }

        [Fact]
        public void GraphStreaming_RoundTripsThroughDecoder()
        {
            var sw = new StringWriter { NewLine = "\n" };
            var enc = new StreamEncoder(sw, "ctx");
            enc.WriteSymbol(new Symbol { QualifiedName = "pkg.A", Kind = "function", Score = 0.9, Provenance = "lsp", Distance = 0 });
            enc.WriteSymbol(new Symbol { QualifiedName = "pkg.B", Kind = "function", Score = 0.4, Provenance = "lsp", Distance = 1 });
            enc.WriteEdge(new Edge { Source = "pkg.B", Target = "pkg.A", EdgeType = "calls" });
            enc.Close();

            var back = Gcf.Decode(sw.ToString());
            Assert.Equal(2, back.Symbols.Count);
            Assert.Single(back.Edges);
        }

        [Fact]
        public void NumberFormatting_MatchesCanonicalForms()
        {
            Assert.Equal("GCF profile=generic\nv=3.14\n", Gcf.EncodeGeneric(Map(("v", 3.14))));
            Assert.Equal("GCF profile=generic\nv=6.022e+23\n", Gcf.EncodeGeneric(Map(("v", 6.022e23))));
            Assert.Equal("GCF profile=generic\nv=1e-7\n", Gcf.EncodeGeneric(Map(("v", 1e-7))));
            Assert.Equal("GCF profile=generic\nv=0.000001\n", Gcf.EncodeGeneric(Map(("v", 1e-6))));
            Assert.Equal("GCF profile=generic\nv=0\n", Gcf.EncodeGeneric(Map(("v", -0.0))));
        }
    }
}
