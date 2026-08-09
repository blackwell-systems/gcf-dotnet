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
    }
}
