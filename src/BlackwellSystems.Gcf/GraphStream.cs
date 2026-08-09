using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BlackwellSystems.Gcf
{
    /// <summary>Options for the graph streaming encoder.</summary>
    public sealed class StreamOptions
    {
        public int TokenBudget { get; set; }
        public int TokensUsed { get; set; }
        public string PackRoot { get; set; } = "";
        public bool Session { get; set; }
        /// <summary>Emit the labeled trailer counts form (counts=targets:2,edges:3) instead of positional.</summary>
        public bool LabeledTrailerCounts { get; set; }
    }

    /// <summary>
    /// Writes GCF graph output incrementally as symbols and edges arrive, with zero
    /// buffering (SPEC 8). A summary trailer is emitted on Close with final counts.
    /// Not thread-safe.
    /// </summary>
    public sealed class StreamEncoder
    {
        private static readonly string[] GroupNames = { "targets", "related", "extended" };
        private readonly TextWriter _w;
        private readonly Dictionary<string, int> _symIndex = new Dictionary<string, int>();
        private int _nextID;
        private string _currentGroup = "";
        private readonly List<(string name, int count)> _groupCounts = new List<(string, int)>();
        private int _edgeCount;
        private bool _edgesStarted;
        private readonly bool _labeled;

        public StreamEncoder(TextWriter writer, string tool, StreamOptions? options = null)
        {
            _w = writer;
            options ??= new StreamOptions();
            _labeled = options.LabeledTrailerCounts;
            var parts = new List<string> { "GCF profile=graph tool=" + tool };
            if (options.TokenBudget > 0) parts.Add("budget=" + options.TokenBudget.ToString(CultureInfo.InvariantCulture));
            if (options.TokensUsed > 0) parts.Add("tokens=" + options.TokensUsed.ToString(CultureInfo.InvariantCulture));
            if (options.PackRoot.Length != 0) parts.Add("pack_root=" + options.PackRoot);
            if (options.Session) parts.Add("session=true");
            _w.Write(string.Join(" ", parts) + "\n");
        }

        private void BumpGroup(string groupName)
        {
            int idx = _groupCounts.FindIndex(g => g.name == groupName);
            if (idx >= 0) _groupCounts[idx] = (groupName, _groupCounts[idx].count + 1);
            else _groupCounts.Add((groupName, 1));
        }

        private string GroupFor(int distance) => distance < GroupNames.Length ? GroupNames[distance] : "distance_" + distance;

        public void WriteSymbol(Symbol s)
        {
            string groupName = GroupFor(s.Distance);
            if (groupName != _currentGroup) { _w.Write("## " + groupName + "\n"); _currentGroup = groupName; }
            int id = _nextID++;
            _symIndex[s.QualifiedName] = id;
            _w.Write("@" + id + " " + KindMap.AbbreviateKind(s.Kind) + " " + s.QualifiedName + " " + GraphCodec.Score2(s.Score) + " " + s.Provenance + "\n");
            BumpGroup(groupName);
        }

        public void WriteEdge(Edge e)
        {
            if (!_symIndex.TryGetValue(e.Source, out int srcIdx)) return;
            if (!_symIndex.TryGetValue(e.Target, out int tgtIdx)) return;
            if (!_edgesStarted) { _w.Write("## edges [?]\n"); _edgesStarted = true; }
            string line = "@" + tgtIdx + "<@" + srcIdx + " " + e.EdgeType;
            if (e.Status.Length != 0 && e.Status != "unchanged") line += " " + e.Status;
            _w.Write(line + "\n");
            _edgeCount++;
        }

        public void WriteBareRef(string qname, int distance)
        {
            string groupName = GroupFor(distance);
            if (groupName != _currentGroup) { _w.Write("## " + groupName + "\n"); _currentGroup = groupName; }
            int id = _nextID++;
            _symIndex[qname] = id;
            _w.Write("@" + id + "  # previously transmitted\n");
            BumpGroup(groupName);
        }

        public void Close()
        {
            var sections = _groupCounts.Where(g => g.count > 0).Select(g => g.name + ":" + g.count).ToList();
            sections.Add("edges:" + _edgeCount);
            string countsStr = _labeled
                ? string.Join(",", sections)
                : string.Join(",", sections.Select(s => s.Substring(s.IndexOf(':') + 1)));
            _w.Write("##! summary symbols=" + _nextID + " edges=" + _edgeCount + " counts=" + countsStr + "\n");
        }

        public int SymbolCount => _nextID;
        public int EdgeCount => _edgeCount;
    }
}
