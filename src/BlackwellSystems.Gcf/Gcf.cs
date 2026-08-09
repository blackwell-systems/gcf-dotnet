using System.Collections.Generic;
namespace BlackwellSystems.Gcf
{
    /// <summary>
    /// Entry points for the GCF (Graph Compact Format) codec. The generic profile
    /// operates on the native value model (null, bool, long, double, string,
    /// <see cref="OrderedMap"/>, IList).
    /// </summary>
    public static class Gcf
    {
        /// <summary>Encode a native value into GCF generic profile.</summary>
        public static string EncodeGeneric(object? data, GenericOptions? options = null)
            => Generic.EncodeGeneric(data, options);

        /// <summary>Decode GCF generic (or graph) profile text into the native value model.</summary>
        public static object? DecodeGeneric(string input)
            => DecodeGenericImpl.DecodeGeneric(input);

        // --- generic delta (SPEC 10a) ---

        /// <summary>Content-addressed pack root (SHA-256) of a keyed set.</summary>
        public static string GenericPackRoot(GenericSet set) => GenericDelta.GenericPackRoot(set);

        /// <summary>Compute the delta from base to next (the blessed producer path).</summary>
        public static GenericDeltaPayload DiffGenericSets(GenericSet baseSet, GenericSet next) => GenericDelta.DiffGenericSets(baseSet, next);

        /// <summary>Encode a delta-participating full base payload.</summary>
        public static string EncodeGenericFull(GenericSet set, string tool) => GenericDelta.EncodeGenericFull(set, tool);

        /// <summary>Serialize a delta payload.</summary>
        public static string EncodeGenericDelta(GenericDeltaPayload delta) => GenericDelta.EncodeGenericDelta(delta);

        /// <summary>Apply a delta to a base and verify the result hashes to expectedNewRoot.</summary>
        public static GenericSet VerifyGenericDelta(GenericSet baseSet, GenericDeltaPayload delta, string expectedNewRoot) => GenericDelta.VerifyGenericDelta(baseSet, delta, expectedNewRoot);

        /// <summary>Decode a full base payload into a set plus its declared pack_root.</summary>
        public static (GenericSet set, string packRoot) DecodeGenericFull(string text) => GenericDelta.DecodeGenericFull(text);

        /// <summary>Decode a delta payload.</summary>
        public static GenericDeltaPayload DecodeGenericDelta(string text) => GenericDelta.DecodeGenericDelta(text);

        // --- graph profile ---

        /// <summary>Encode a graph-profile payload into GCF text.</summary>
        public static string Encode(Payload payload) => GraphCodec.Encode(payload);

        /// <summary>Decode GCF graph-profile text into a Payload.</summary>
        public static Payload Decode(string input) => GraphCodec.Decode(input);

        /// <summary>Content-addressed pack root (SHA-256) of a graph snapshot.</summary>
        public static string PackRoot(IReadOnlyList<Symbol> symbols, IReadOnlyList<Edge> edges) => GraphPackRoot.PackRoot(symbols, edges);

        /// <summary>Serialize a graph delta payload.</summary>
        public static string EncodeDelta(DeltaPayload delta) => GraphDelta.EncodeDelta(delta);

        /// <summary>Decode a graph delta wire payload.</summary>
        public static DeltaPayload DecodeDelta(string wire) => GraphDelta.DecodeDelta(wire);

        /// <summary>Encode a graph payload with session dedup (bare @N references for previously-transmitted symbols).</summary>
        public static string EncodeWithSession(Payload payload, Session? session) => SessionCodec.EncodeWithSession(payload, session);

        /// <summary>Apply a graph delta to a base snapshot and verify the recomputed root.</summary>
        public static (IReadOnlyList<Symbol> symbols, IReadOnlyList<Edge> edges) VerifyDelta(
            IReadOnlyList<Symbol> baseSymbols, IReadOnlyList<Edge> baseEdges,
            IReadOnlyList<Symbol> removed, IReadOnlyList<Symbol> added,
            IReadOnlyList<Edge> removedEdges, IReadOnlyList<Edge> addedEdges, string expectedNewRoot)
        {
            var (s, e) = GraphDelta.VerifyDelta(baseSymbols, baseEdges, removed, added, removedEdges, addedEdges, expectedNewRoot);
            return (s, e);
        }
    }
}
