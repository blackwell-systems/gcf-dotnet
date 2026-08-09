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

        /// <summary>
        /// Decode GCF from raw UTF-8 bytes. .NET strings are always valid UTF-16, so
        /// byte-level UTF-8 validity (the spec's invalid_utf8 error) can only be enforced
        /// at this boundary; malformed input is rejected before decoding. This mirrors the
        /// utf8.ValidString check the Go/Rust decoders perform on their byte-backed strings.
        /// </summary>
        public static object? DecodeGeneric(byte[] utf8) => DecodeGenericImpl.DecodeGeneric(Utf8Strict(utf8));

        /// <summary>Decode a graph payload from raw UTF-8 bytes (strict UTF-8; see the generic overload).</summary>
        public static Payload Decode(byte[] utf8) => GraphCodec.Decode(Utf8Strict(utf8));

        private static string Utf8Strict(byte[] bytes)
        {
            try
            {
                return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new DecodeException("invalid_utf8: malformed UTF-8 byte sequence");
            }
        }

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
