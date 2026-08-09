using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackwellSystems.Gcf
{
    /// <summary>
    /// Tracks symbols transmitted to a client, enabling later responses to reference
    /// them by session-global ID without full retransmission (SPEC 9). Thread-safe.
    /// </summary>
    public sealed class Session
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, int> _symbols = new Dictionary<string, int>();
        private int _nextID;

        /// <summary>True if the symbol was sent in a previous response.</summary>
        public bool Transmitted(string qname) { lock (_lock) return _symbols.ContainsKey(qname); }

        /// <summary>Session-global ID for a previously transmitted symbol, or -1.</summary>
        public int GetID(string qname) { lock (_lock) return _symbols.TryGetValue(qname, out var v) ? v : -1; }

        /// <summary>Register newly-sent symbols, assigning session-global IDs.</summary>
        public void Record(IEnumerable<Symbol> symbolList)
        {
            lock (_lock)
                foreach (var sym in symbolList)
                    if (!_symbols.ContainsKey(sym.QualifiedName)) _symbols[sym.QualifiedName] = _nextID++;
        }

        /// <summary>Number of symbols tracked.</summary>
        public int Size() { lock (_lock) return _symbols.Count; }

        /// <summary>Clear session state.</summary>
        public void Reset() { lock (_lock) { _symbols.Clear(); _nextID = 0; } }
    }

    internal static class SessionCodec
    {
        private static readonly string[] GroupNames = { "targets", "related", "extended" };

        public static string EncodeWithSession(Payload payload, Session? session)
        {
            if (session == null) return GraphCodec.Encode(payload);

            var b = new StringBuilder();

            var newThisCall = new HashSet<string>();
            var newSymbols = new List<Symbol>();
            foreach (var s in payload.Symbols)
                if (!session.Transmitted(s.QualifiedName)) { newThisCall.Add(s.QualifiedName); newSymbols.Add(s); }
            session.Record(newSymbols);

            int IdOf(string qname) => session.GetID(qname);

            var symbolNames = new HashSet<string>(payload.Symbols.Select(s => s.QualifiedName));
            int validEdges = payload.Edges.Count(e => symbolNames.Contains(e.Source) && symbolNames.Contains(e.Target));

            var parts = new List<string> { "GCF profile=graph tool=" + payload.Tool };
            if (payload.TokenBudget > 0) parts.Add("budget=" + payload.TokenBudget);
            if (payload.TokensUsed > 0) parts.Add("tokens=" + payload.TokensUsed);
            parts.Add("symbols=" + payload.Symbols.Count);
            if (validEdges > 0) parts.Add("edges=" + validEdges);
            if (payload.PackRoot.Length != 0) parts.Add("pack_root=" + payload.PackRoot);
            parts.Add("session=true");
            b.Append(string.Join(" ", parts)).Append('\n');

            var groups = GroupByDistance(payload.Symbols);
            foreach (var g in groups)
            {
                if (g.Count == 0) continue;
                int dist = g[0].Distance;
                string name = dist < GroupNames.Length ? GroupNames[dist] : "distance_" + dist;
                b.Append("## ").Append(name).Append('\n');
                foreach (var s in g)
                {
                    int id = IdOf(s.QualifiedName);
                    if (!newThisCall.Contains(s.QualifiedName))
                        b.Append('@').Append(id).Append("  # previously transmitted\n");
                    else
                        b.Append('@').Append(id).Append(' ').Append(KindMap.AbbreviateKind(s.Kind)).Append(' ')
                         .Append(s.QualifiedName).Append(' ').Append(GraphCodec.Score2(s.Score)).Append(' ').Append(s.Provenance).Append('\n');
                }
            }

            if (payload.Edges.Count != 0)
            {
                b.Append("## edges [").Append(validEdges).Append("]\n");
                foreach (var e in payload.Edges)
                {
                    if (!symbolNames.Contains(e.Source) || !symbolNames.Contains(e.Target)) continue;
                    b.Append('@').Append(IdOf(e.Target)).Append("<@").Append(IdOf(e.Source)).Append(' ').Append(e.EdgeType);
                    if (e.Status.Length != 0 && e.Status != "unchanged") b.Append(' ').Append(e.Status);
                    b.Append('\n');
                }
            }

            return b.ToString();
        }

        // Group by distance asc, score desc (stable) — same ordering as GraphCodec.
        private static List<List<Symbol>> GroupByDistance(IReadOnlyList<Symbol> symbols)
        {
            var groups = new List<List<Symbol>>();
            if (symbols.Count == 0) return groups;
            var sorted = symbols.Select((s, i) => (s, i))
                .OrderBy(t => t.s.Distance).ThenByDescending(t => t.s.Score).ThenBy(t => t.i)
                .Select(t => t.s).ToList();
            int currentDistance = -1;
            List<Symbol> current = new List<Symbol>();
            foreach (var s in sorted)
            {
                if (s.Distance != currentDistance)
                {
                    if (current.Count != 0) groups.Add(current);
                    currentDistance = s.Distance;
                    current = new List<Symbol>();
                }
                current.Add(s);
            }
            if (current.Count != 0) groups.Add(current);
            return groups;
        }
    }
}
