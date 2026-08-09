using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;
using Xunit;

namespace BlackwellSystems.Gcf.Tests
{
    public class GenericEncodeConformanceTests
    {
        // Categories that exercise the generic encoder (graph/delta/streaming are
        // separate profiles ported in later phases).
        private static readonly string[] GenericCategories =
        {
            "scalar", "numbers", "keys", "whitespace", "arrays", "containers",
            "roots", "flatten", "attachments", "inline-schema", "keyed-map"
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

        public static IEnumerable<object[]> EncodeFixtures()
        {
            var root = ConformanceDir();
            foreach (var cat in GenericCategories)
            {
                var dir = Path.Combine(root, cat);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    var op = r.TryGetProperty("operation", out var opv) ? opv.GetString() : null;
                    if (op != "encode") continue;
                    yield return new object[] { cat + "/" + Path.GetFileName(file) };
                }
            }
        }

        [Theory]
        [MemberData(nameof(EncodeFixtures))]
        public void Encode(string relative)
        {
            var root = ConformanceDir();
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;

            var input = ConformanceInput.FromJson(r.GetProperty("input"));
            var expected = r.GetProperty("expected").GetString();

            var opts = new GenericOptions();
            if (r.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty("noFlatten", out var nf) && nf.ValueKind == JsonValueKind.True)
            {
                opts.NoFlatten = true;
            }

            var actual = Gcf.EncodeGeneric(input, opts);
            Assert.Equal(expected, actual);
        }
    }
}
