using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackwellSystems.Gcf
{
    internal static class GraphDelta
    {
        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);

        public static string EncodeDelta(DeltaPayload delta)
        {
            var b = new StringBuilder();
            double savings = delta.FullTokens > 0 ? 100.0 * (1.0 - (double)delta.DeltaTokens / delta.FullTokens) : 0.0;
            b.Append("GCF profile=graph tool=").Append(delta.Tool)
             .Append(" delta=true base_root=").Append(delta.BaseRoot)
             .Append(" new_root=").Append(delta.NewRoot)
             .Append(" tokens=").Append(delta.DeltaTokens)
             .Append(" savings=").Append(Math.Round(savings, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)).Append("%\n");

            if (delta.Removed.Count != 0)
            {
                b.Append("## removed\n");
                foreach (var s in delta.Removed)
                    b.Append(KindMap.AbbreviateKind(s.Kind)).Append(' ').Append(s.QualifiedName).Append('\n');
            }
            if (delta.Added.Count != 0)
            {
                b.Append("## added\n");
                for (int i = 0; i < delta.Added.Count; i++)
                {
                    var s = delta.Added[i];
                    b.Append('@').Append(i).Append(' ').Append(KindMap.AbbreviateKind(s.Kind)).Append(' ')
                     .Append(s.QualifiedName).Append(' ').Append(GraphCodec.Score2(s.Score)).Append(' ')
                     .Append(s.Provenance).Append(' ').Append(s.Distance).Append('\n');
                }
            }
            if (delta.RemovedEdges.Count != 0)
            {
                b.Append("## edges_removed\n");
                foreach (var e in delta.RemovedEdges)
                    b.Append(e.Source).Append(" -> ").Append(e.Target).Append(' ').Append(e.EdgeType).Append('\n');
            }
            if (delta.AddedEdges.Count != 0)
            {
                b.Append("## edges_added\n");
                foreach (var e in delta.AddedEdges)
                    b.Append(e.Source).Append(" -> ").Append(e.Target).Append(' ').Append(e.EdgeType).Append('\n');
            }
            return b.ToString();
        }

        private static Edge ParseDeltaEdge(string line)
        {
            int idx = line.IndexOf(" -> ", StringComparison.Ordinal);
            if (idx < 0) throw new DecodeException("malformed_delta: edge line missing ' -> ': \"" + line + "\"");
            string source = line.Substring(0, idx);
            var rest = WsRe.Split(line.Substring(idx + 4).Trim()).Where(x => x.Length != 0).ToList();
            if (rest.Count != 2) throw new DecodeException("malformed_delta: edge line \"" + line + "\" must be 'source -> target type'");
            return new Edge { Source = source, Target = rest[0], EdgeType = rest[1] };
        }

        public static DeltaPayload DecodeDelta(string wire)
        {
            var lines = wire.TrimEnd('\n').Split('\n');
            if (lines.Length == 0 || lines[0].Length == 0) throw new DecodeException("missing_header: empty delta payload");
            string header = lines[0].TrimEnd('\r');
            if (!header.StartsWith("GCF profile=graph", StringComparison.Ordinal))
                throw new DecodeException("missing_profile: delta header must begin with 'GCF profile=graph'");

            string tool = "", baseRoot = "", newRoot = "";
            foreach (var field in WsRe.Split(header.Trim()).Where(x => x.Length != 0))
            {
                int eq = field.IndexOf('=');
                if (eq < 0) continue;
                string k = field.Substring(0, eq), v = field.Substring(eq + 1);
                if (k == "tool") tool = v; else if (k == "base_root") baseRoot = v; else if (k == "new_root") newRoot = v;
            }

            var removed = new List<Symbol>();
            var added = new List<Symbol>();
            var removedEdges = new List<Edge>();
            var addedEdges = new List<Edge>();
            string section = "";

            for (int li = 1; li < lines.Length; li++)
            {
                string line = lines[li].TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    section = line.Substring(3).Trim();
                    if (section != "removed" && section != "added" && section != "edges_removed" && section != "edges_added")
                        throw new DecodeException("malformed_delta: unknown section \"" + section + "\"");
                    continue;
                }
                switch (section)
                {
                    case "removed":
                        {
                            var parts = WsRe.Split(line.Trim()).Where(x => x.Length != 0).ToList();
                            if (parts.Count != 2) throw new DecodeException("malformed_delta: removed line \"" + line + "\" must be 'kind qname'");
                            removed.Add(new Symbol { Kind = KindMap.ExpandKind(parts[0]), QualifiedName = parts[1] });
                            break;
                        }
                    case "added":
                        {
                            var parts = WsRe.Split(line.Trim()).Where(x => x.Length != 0).ToList();
                            if (parts.Count != 6) throw new DecodeException("malformed_delta: added line \"" + line + "\" must be '@id kind qname score provenance distance'");
                            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
                                throw new DecodeException("malformed_delta: invalid added score \"" + parts[3] + "\"");
                            if (!int.TryParse(parts[5], out int dist))
                                throw new DecodeException("malformed_delta: invalid added distance \"" + parts[5] + "\"");
                            added.Add(new Symbol { Kind = KindMap.ExpandKind(parts[1]), QualifiedName = parts[2], Score = score, Provenance = parts[4], Distance = dist });
                            break;
                        }
                    case "edges_removed": removedEdges.Add(ParseDeltaEdge(line)); break;
                    case "edges_added": addedEdges.Add(ParseDeltaEdge(line)); break;
                    default: throw new DecodeException("malformed_delta: data line \"" + line + "\" before any section header");
                }
            }

            return new DeltaPayload
            {
                Tool = tool, BaseRoot = baseRoot, NewRoot = newRoot,
                Removed = removed, Added = added, RemovedEdges = removedEdges, AddedEdges = addedEdges
            };
        }

        public static (List<Symbol> symbols, List<Edge> edges) VerifyDelta(
            IReadOnlyList<Symbol> baseSymbols, IReadOnlyList<Edge> baseEdges,
            IReadOnlyList<Symbol> removed, IReadOnlyList<Symbol> added,
            IReadOnlyList<Edge> removedEdges, IReadOnlyList<Edge> addedEdges, string expectedNewRoot)
        {
            var symMap = new Dictionary<(string, string), Symbol>();
            var symOrder = new List<(string, string)>();
            foreach (var s in baseSymbols) { var key = (s.Kind, s.QualifiedName); if (!symMap.ContainsKey(key)) symOrder.Add(key); symMap[key] = s; }

            foreach (var s in removed)
            {
                var key = (s.Kind, s.QualifiedName);
                if (!symMap.ContainsKey(key)) throw new DecodeException("delta_invalid: removing symbol " + s.Kind + " " + s.QualifiedName + " that does not exist in base");
                symMap.Remove(key); symOrder.Remove(key);
            }
            foreach (var s in added)
            {
                var key = (s.Kind, s.QualifiedName);
                if (symMap.ContainsKey(key)) throw new DecodeException("delta_invalid: adding symbol " + s.Kind + " " + s.QualifiedName + " that already exists");
                symMap[key] = s; symOrder.Add(key);
            }
            var resultSymbols = symOrder.Select(k => symMap[k]).ToList();

            var edgeMap = new Dictionary<(string, string, string), Edge>();
            var edgeOrder = new List<(string, string, string)>();
            foreach (var e in baseEdges) { var key = (e.Source, e.Target, e.EdgeType); if (!edgeMap.ContainsKey(key)) edgeOrder.Add(key); edgeMap[key] = e; }

            foreach (var e in removedEdges)
            {
                var key = (e.Source, e.Target, e.EdgeType);
                if (!edgeMap.ContainsKey(key)) throw new DecodeException("delta_invalid: removing edge " + e.Source + " -> " + e.Target + " " + e.EdgeType + " that does not exist");
                edgeMap.Remove(key); edgeOrder.Remove(key);
            }
            foreach (var e in addedEdges)
            {
                var key = (e.Source, e.Target, e.EdgeType);
                if (edgeMap.ContainsKey(key)) throw new DecodeException("delta_invalid: adding edge " + e.Source + " -> " + e.Target + " " + e.EdgeType + " that already exists");
                edgeMap[key] = e; edgeOrder.Add(key);
            }
            var resultEdges = edgeOrder.Select(k => edgeMap[k]).ToList();

            string computed = GraphPackRoot.PackRoot(resultSymbols, resultEdges);
            if (computed != expectedNewRoot) throw new DecodeException("root_mismatch: computed " + computed + ", expected " + expectedNewRoot);
            return (resultSymbols, resultEdges);
        }
    }
}
