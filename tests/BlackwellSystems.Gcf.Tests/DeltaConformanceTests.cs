using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    public class DeltaConformanceTests
    {
        private static readonly string[] Categories =
        {
            "generic-pack-root", "generic-delta", "generic-delta-session"
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
            var input = r.GetProperty("input");

            switch (op)
            {
                case "generic-pack-root":
                    Assert.Equal(r.GetProperty("expected").GetString(), Gcf.GenericPackRoot(SetFromJson(input)));
                    break;

                case "generic-delta":
                    Assert.Equal(r.GetProperty("expected").GetString(), Gcf.EncodeGenericDelta(DeltaFromJson(input)));
                    break;

                case "generic-delta-decode":
                    {
                        var wire = input.GetProperty("wire").GetString()!;
                        var baseSet = SetFromJson(input.GetProperty("base"));
                        var expNewRoot = input.GetProperty("expectedNewRoot").GetString()!;
                        if (r.TryGetProperty("expectedError", out _))
                        {
                            Assert.ThrowsAny<Exception>(() =>
                            {
                                var d0 = Gcf.DecodeGenericDelta(wire);
                                Gcf.VerifyGenericDelta(baseSet, d0, expNewRoot);
                            });
                            break;
                        }
                        var d = Gcf.DecodeGenericDelta(wire);
                        var result = Gcf.VerifyGenericDelta(baseSet, d, expNewRoot);
                        Assert.Equal(r.GetProperty("expected").GetString(), Gcf.GenericPackRoot(result));
                        break;
                    }

                case "generic-delta-verify":
                    {
                        var baseSet = SetFromJson(input.GetProperty("base"));
                        var d = DeltaFromJson(input.GetProperty("delta"));
                        var expNewRoot = input.TryGetProperty("expectedNewRoot", out var enr) ? enr.GetString()! : d.NewRoot;
                        if (r.TryGetProperty("expectedError", out _))
                        {
                            Assert.ThrowsAny<Exception>(() => Gcf.VerifyGenericDelta(baseSet, d, expNewRoot));
                            break;
                        }
                        var result = Gcf.VerifyGenericDelta(baseSet, d, expNewRoot);
                        Assert.Equal(r.GetProperty("expected").GetString(), Gcf.GenericPackRoot(result));
                        break;
                    }

                case "generic-delta-session":
                    {
                        var baseSet = SetFromJson(input.GetProperty("base"));
                        var tool = input.TryGetProperty("tool", out var t) ? t.GetString()! : "";
                        var policy = PolicyFromJson(input.GetProperty("policy"));
                        var session = new GenericDeltaSession(baseSet, tool, policy);

                        var exp = r.GetProperty("expected");
                        Assert.Equal(exp.GetProperty("initialFull").GetString(), session.CurrentFull());

                        var updates = input.GetProperty("updates").EnumerateArray().Select(SetFromJson).ToList();
                        var emissions = exp.GetProperty("emissions").EnumerateArray().ToList();
                        Assert.Equal(emissions.Count, updates.Count);
                        for (int i = 0; i < updates.Count; i++)
                        {
                            var (wire, isFull) = session.Next(updates[i]);
                            Assert.Equal(emissions[i].GetProperty("wire").GetString(), wire);
                            Assert.Equal(emissions[i].GetProperty("isFull").GetBoolean(), isFull);
                        }
                        break;
                    }

                default:
                    throw new Xunit.Sdk.XunitException("unknown delta operation: " + op);
            }
        }

        internal static GenericSet SetFromJson(JsonElement e)
        {
            string key = e.GetProperty("key").GetString()!;
            var fields = e.GetProperty("fields").EnumerateArray().Select(x => x.GetString()!).ToList();
            var rows = e.GetProperty("rows").EnumerateArray().Select(x => (OrderedMap)ConformanceInput.FromJson(x)!).ToList();
            string name = e.TryGetProperty("name", out var nm) ? nm.GetString()! : "";
            return new GenericSet(key, fields, rows, name);
        }

        private static GenericDeltaPayload DeltaFromJson(JsonElement e)
        {
            List<OrderedMap> Arr(string prop) => e.TryGetProperty(prop, out var a)
                ? a.EnumerateArray().Select(x => (OrderedMap)ConformanceInput.FromJson(x)!).ToList()
                : new List<OrderedMap>();

            return new GenericDeltaPayload
            {
                Tool = e.TryGetProperty("tool", out var t) ? t.GetString()! : "",
                Key = e.GetProperty("key").GetString()!,
                Fields = e.GetProperty("fields").EnumerateArray().Select(x => x.GetString()!).ToList(),
                BaseRoot = e.TryGetProperty("baseRoot", out var br) ? br.GetString()! : "",
                NewRoot = e.TryGetProperty("newRoot", out var nr) ? nr.GetString()! : "",
                Added = Arr("added"),
                Changed = Arr("changed"),
                Removed = e.TryGetProperty("removed", out var rm) ? rm.EnumerateArray().Select(ConformanceInput.FromJson).ToList() : new List<object?>(),
                DeltaTokens = e.TryGetProperty("deltaTokens", out var dt) ? dt.GetInt32() : 0,
                FullTokens = e.TryGetProperty("fullTokens", out var ft) ? ft.GetInt32() : 0,
            };
        }

        private static ReanchorPolicy PolicyFromJson(JsonElement e)
        {
            var mode = e.GetProperty("mode").GetString();
            if (mode == "sizeGuard") return ReanchorPolicy.SizeGuard();
            int n = e.TryGetProperty("n", out var nn) ? nn.GetInt32() : 0;
            return ReanchorPolicy.FixedN(n);
        }
    }
}
