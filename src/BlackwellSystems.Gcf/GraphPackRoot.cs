using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackwellSystems.Gcf
{
    internal static class GraphPackRoot
    {
        // gcf-pack-root-v1, graph profile (SPEC 10.2). Byte-for-byte interoperable
        // with gcf-go packroot.go and the other SDKs.
        public static string PackRoot(IReadOnlyList<Symbol> symbols, IReadOnlyList<Edge> edges)
        {
            var cmp = GenericDelta.ByteOrderComparer.Instance;

            var symRecords = symbols.Select(s =>
            {
                string kind = KindMap.Abbrev.TryGetValue(s.Kind, out var k) ? k : s.Kind;
                string score = Scalar.FormatNumberValue(s.Score);
                return "S\t" + kind + "\t" + s.QualifiedName + "\t" + score + "\t" + s.Provenance + "\t" + s.Distance + "\n";
            }).OrderBy(x => x, cmp);

            var symKindMap = new Dictionary<string, string>(symbols.Count);
            foreach (var s in symbols)
                symKindMap[s.QualifiedName] = KindMap.Abbrev.TryGetValue(s.Kind, out var k) ? k : s.Kind;

            var edgeRecords = edges.Select(e =>
            {
                string srcKind = symKindMap.TryGetValue(e.Source, out var sk) ? sk : "";
                string tgtKind = symKindMap.TryGetValue(e.Target, out var tk) ? tk : "";
                return "E\t" + srcKind + "\t" + e.Source + "\t" + tgtKind + "\t" + e.Target + "\t" + e.EdgeType + "\n";
            }).OrderBy(x => x, cmp);

            string canonical = string.Concat(symRecords) + string.Concat(edgeRecords);
            return "sha256:" + GenericDelta.Sha256Hex(canonical);
        }
    }
}
