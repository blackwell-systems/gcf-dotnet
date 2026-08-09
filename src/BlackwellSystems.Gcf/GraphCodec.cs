using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackwellSystems.Gcf
{
    internal static class GraphCodec
    {
        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly string[] GroupNames = { "targets", "related", "extended" };

        internal static string Score2(double score) => score.ToString("F2", CultureInfo.InvariantCulture);

        private sealed class DistanceGroup
        {
            public int Distance; public List<Symbol> Symbols;
            public DistanceGroup(int d, List<Symbol> s) { Distance = d; Symbols = s; }
        }

        private static List<DistanceGroup> GroupByDistance(IReadOnlyList<Symbol> symbols)
        {
            var groups = new List<DistanceGroup>();
            if (symbols.Count == 0) return groups;
            // distance asc, then score desc (stable).
            var sorted = symbols
                .Select((s, i) => (s, i))
                .OrderBy(t => t.s.Distance)
                .ThenByDescending(t => t.s.Score)
                .ThenBy(t => t.i)
                .Select(t => t.s).ToList();

            int currentDistance = -1;
            List<Symbol> current = new List<Symbol>();
            foreach (var s in sorted)
            {
                if (s.Distance != currentDistance)
                {
                    if (current.Count != 0) groups.Add(new DistanceGroup(currentDistance, current));
                    currentDistance = s.Distance;
                    current = new List<Symbol>();
                }
                current.Add(s);
            }
            if (current.Count != 0) groups.Add(new DistanceGroup(currentDistance, current));
            return groups;
        }

        public static string Encode(Payload payload)
        {
            var b = new StringBuilder();
            var groups = GroupByDistance(payload.Symbols);

            var symIndex = new Dictionary<string, int>();
            int nextID = 0;
            foreach (var g in groups)
                foreach (var s in g.Symbols)
                    symIndex[s.QualifiedName] = nextID++;

            int validEdges = payload.Edges.Count(e => symIndex.ContainsKey(e.Source) && symIndex.ContainsKey(e.Target));

            b.Append("GCF profile=graph tool=").Append(payload.Tool);
            if (payload.TokenBudget > 0) b.Append(" budget=").Append(payload.TokenBudget);
            if (payload.TokensUsed > 0) b.Append(" tokens=").Append(payload.TokensUsed);
            b.Append(" symbols=").Append(payload.Symbols.Count);
            if (validEdges > 0) b.Append(" edges=").Append(validEdges);
            if (payload.PackRoot.Length != 0) b.Append(" pack_root=").Append(payload.PackRoot);
            b.Append('\n');

            foreach (var g in groups)
            {
                if (g.Symbols.Count == 0) continue;
                string name = g.Distance < GroupNames.Length ? GroupNames[g.Distance] : "distance_" + g.Distance;
                b.Append("## ").Append(name).Append('\n');
                foreach (var s in g.Symbols)
                {
                    if (!symIndex.TryGetValue(s.QualifiedName, out int idx)) continue;
                    b.Append('@').Append(idx).Append(' ').Append(KindMap.AbbreviateKind(s.Kind)).Append(' ')
                     .Append(s.QualifiedName).Append(' ').Append(Score2(s.Score)).Append(' ').Append(s.Provenance).Append('\n');
                }
            }

            if (payload.Edges.Count != 0)
            {
                var resolved = payload.Edges
                    .Select(e => symIndex.TryGetValue(e.Source, out int si) && symIndex.TryGetValue(e.Target, out int ti)
                        ? (si, ti, e.EdgeType, e.Status, ok: true) : (0, 0, "", "", ok: false))
                    .Where(t => t.ok)
                    .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3, StringComparer.Ordinal)
                    .ToList();

                b.Append("## edges [").Append(validEdges).Append("]\n");
                foreach (var e in resolved)
                {
                    b.Append('@').Append(e.Item2).Append("<@").Append(e.Item1).Append(' ').Append(e.Item3);
                    if (e.Item4.Length != 0 && e.Item4 != "unchanged") b.Append(' ').Append(e.Item4);
                    b.Append('\n');
                }
            }

            return b.ToString();
        }

        public static Payload Decode(string input)
        {
            var lines = input.Split('\n');
            if (lines.Length == 0) throw new DecodeException("gcf: empty input");
            string header = lines[0];
            if (!header.StartsWith("GCF ", StringComparison.Ordinal))
                throw new DecodeException("gcf: invalid header, expected 'GCF ...' got \"" + header + "\"");

            var (tool, tokenBudget, tokensUsed, packRoot) = ParseHeader(header.Substring(4));

            var symbols = new List<Symbol>();
            var symByID = new Dictionary<int, Symbol>();
            int currentDistance = 0;
            bool inEdges = false;
            int declaredEdges = -1;
            bool edgesDeclared = false;
            var edges = new List<Edge>();
            bool isDelta = header.Contains("delta=true");
            var validDeltaSections = new HashSet<string> { "removed", "added", "edges_removed", "edges_added" };

            for (int li = 1; li < lines.Length; li++)
            {
                string trimmed = lines[li].TrimEnd('\r');
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("##! ", StringComparison.Ordinal)) continue;

                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    string group = trimmed.Substring(3);
                    int declaredCount = -1;
                    int bracketIdx = group.IndexOf(" [", StringComparison.Ordinal);
                    if (bracketIdx >= 0)
                    {
                        string bracket = group.Substring(bracketIdx + 2);
                        group = group.Substring(0, bracketIdx);
                        int end = bracket.IndexOf(']');
                        if (end >= 0)
                        {
                            string cntStr = bracket.Substring(0, end);
                            if (cntStr != "?")
                            {
                                if (!int.TryParse(cntStr, out declaredCount))
                                    throw new DecodeException("count_mismatch: invalid section count \"" + cntStr + "\"");
                            }
                        }
                    }
                    if (isDelta && !validDeltaSections.Contains(group))
                        throw new DecodeException("malformed_delta: invalid delta section \"" + group + "\"");
                    inEdges = group == "edges";
                    if (inEdges && declaredCount >= 0) { declaredEdges = declaredCount; edgesDeclared = true; }
                    if (!inEdges)
                    {
                        if (group == "targets") currentDistance = 0;
                        else if (group == "related") currentDistance = 1;
                        else if (group == "extended") currentDistance = 2;
                        else if (group.StartsWith("distance_", StringComparison.Ordinal))
                        {
                            if (int.TryParse(group.Substring(9), out int dd)) currentDistance = dd;
                        }
                    }
                    continue;
                }

                if (trimmed.StartsWith("# ", StringComparison.Ordinal)) continue;

                if (inEdges) edges.Add(ParseEdgeLine(trimmed, symByID));
                else
                {
                    var (sym, id) = ParseSymbolLine(trimmed, currentDistance);
                    symbols.Add(sym);
                    symByID[id] = sym;
                }
            }

            if (edgesDeclared && edges.Count != declaredEdges)
                throw new DecodeException("count_mismatch: declared " + declaredEdges + " edges, got " + edges.Count);

            return new Payload
            {
                Tool = tool, TokenBudget = tokenBudget, TokensUsed = tokensUsed, PackRoot = packRoot,
                Symbols = symbols, Edges = edges
            };
        }

        private static (string tool, int budget, int tokens, string packRoot) ParseHeader(string fields)
        {
            string tool = "", packRoot = "";
            int budget = 0, tokens = 0;
            foreach (var part in WsRe.Split(fields.Trim()))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                switch (k)
                {
                    case "tool": tool = v; break;
                    case "budget": if (!int.TryParse(v, out budget)) throw new DecodeException("gcf: invalid budget \"" + v + "\""); break;
                    case "tokens": if (!int.TryParse(v, out tokens)) throw new DecodeException("gcf: invalid tokens \"" + v + "\""); break;
                    case "pack_root": packRoot = v; break;
                }
            }
            return (tool, budget, tokens, packRoot);
        }

        private static (Symbol sym, int id) ParseSymbolLine(string line, int distance)
        {
            if (!line.StartsWith("@", StringComparison.Ordinal))
                throw new DecodeException("invalid_node_line: expected symbol line starting with @, got \"" + line + "\"");
            var parts = WsRe.Split(line.Trim());
            if (parts.Length < 5)
                throw new DecodeException("invalid_node_line: symbol line needs at least 5 fields, got " + parts.Length + " in \"" + line + "\"");
            string idStr = parts[0].Substring(1);
            if (!int.TryParse(idStr, out int id)) throw new DecodeException("invalid_symbol_id: invalid symbol id \"" + idStr + "\"");
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
                throw new DecodeException("invalid_score: invalid score \"" + parts[3] + "\"");
            var sym = new Symbol
            {
                QualifiedName = parts[2], Kind = KindMap.ExpandKind(parts[1]),
                Score = score, Provenance = parts[4], Distance = distance
            };
            return (sym, id);
        }

        private static Edge ParseEdgeLine(string line, Dictionary<int, Symbol> symByID)
        {
            var parts = WsRe.Split(line.Trim());
            if (parts.Length < 2) throw new DecodeException("gcf: edge line needs at least 2 fields, got \"" + line + "\"");
            string reff = parts[0];
            int ltIdx = reff.IndexOf('<');
            if (ltIdx < 0) throw new DecodeException("invalid_edge_syntax: edge line missing '<' separator in \"" + reff + "\"");
            string targetIDStr = reff.Substring(1, ltIdx - 1);
            string sourceIDStr = reff.Substring(ltIdx + 2);
            if (!int.TryParse(targetIDStr, out int targetID)) throw new DecodeException("gcf: invalid target id \"" + targetIDStr + "\"");
            if (!int.TryParse(sourceIDStr, out int sourceID)) throw new DecodeException("gcf: invalid source id \"" + sourceIDStr + "\"");
            if (!symByID.TryGetValue(targetID, out var targetSym) || !symByID.TryGetValue(sourceID, out var sourceSym))
                throw new DecodeException("unknown_edge_reference: edge references unknown symbol id(s): target=" + targetID + " source=" + sourceID);
            return new Edge
            {
                Source = sourceSym.QualifiedName, Target = targetSym.QualifiedName,
                EdgeType = parts[1], Status = parts.Length >= 3 ? parts[2] : ""
            };
        }
    }
}
