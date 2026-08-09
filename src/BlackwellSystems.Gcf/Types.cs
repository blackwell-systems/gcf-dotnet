using System.Collections.Generic;

namespace BlackwellSystems.Gcf
{
    /// <summary>A node in a GCF graph-profile payload.</summary>
    public sealed class Symbol
    {
        public string QualifiedName { get; set; } = "";
        public string Kind { get; set; } = "";
        public double Score { get; set; }
        public string Provenance { get; set; } = "";
        public int Distance { get; set; }
        public string Signature { get; set; } = "";
    }

    /// <summary>A directed relationship in a GCF graph-profile payload.</summary>
    public sealed class Edge
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string EdgeType { get; set; } = "";
        public string Status { get; set; } = "";
    }

    /// <summary>Input/output structure for GCF graph-profile encoding/decoding.</summary>
    public sealed class Payload
    {
        public string Tool { get; set; } = "";
        public int TokensUsed { get; set; }
        public int TokenBudget { get; set; }
        public string PackRoot { get; set; } = "";
        public List<Symbol> Symbols { get; set; } = new List<Symbol>();
        public List<Edge> Edges { get; set; } = new List<Edge>();
    }

    /// <summary>The diff between a prior context pack and the current result (graph profile).</summary>
    public sealed class DeltaPayload
    {
        public string Tool { get; set; } = "";
        public string BaseRoot { get; set; } = "";
        public string NewRoot { get; set; } = "";
        public List<Symbol> Removed { get; set; } = new List<Symbol>();
        public List<Symbol> Added { get; set; } = new List<Symbol>();
        public List<Edge> RemovedEdges { get; set; } = new List<Edge>();
        public List<Edge> AddedEdges { get; set; } = new List<Edge>();
        public int DeltaTokens { get; set; }
        public int FullTokens { get; set; }
    }

    // Temporary stub: the graph profile decoder is ported in a later phase. Generic
    // fixtures never reach this path (the header profile gates it).
    internal static class GraphDecode
    {
        public static Payload Decode(string input)
            => throw new DecodeException("graph profile decode not yet implemented");
    }
}
