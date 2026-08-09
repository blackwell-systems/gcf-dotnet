using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackwellSystems.Gcf
{
    /// <summary>
    /// A keyed record set: the unit generic-profile delta operates on (SPEC 10a).
    /// Rows are order-agnostic (set semantics); Fields carries the declared column
    /// order for the wire; Key names the identity column; Name is the section name.
    /// </summary>
    public sealed class GenericSet
    {
        public string Key { get; }
        public IReadOnlyList<string> Fields { get; }
        public IReadOnlyList<OrderedMap> Rows { get; }
        public string Name { get; }

        public GenericSet(string key, IReadOnlyList<string> fields, IReadOnlyList<OrderedMap> rows, string name = "")
        {
            Key = key; Fields = fields; Rows = rows; Name = name;
        }
    }

    /// <summary>A diff between two <see cref="GenericSet"/>s (SPEC 10a).</summary>
    public sealed class GenericDeltaPayload
    {
        public string Key { get; set; } = "";
        public IReadOnlyList<string> Fields { get; set; } = new List<string>();
        public string BaseRoot { get; set; } = "";
        public string NewRoot { get; set; } = "";
        public IReadOnlyList<OrderedMap> Added { get; set; } = new List<OrderedMap>();
        public IReadOnlyList<OrderedMap> Changed { get; set; } = new List<OrderedMap>();
        public IReadOnlyList<object?> Removed { get; set; } = new List<object?>();
        public string Tool { get; set; } = "";
        public int DeltaTokens { get; set; }
        public int FullTokens { get; set; }
    }

    /// <summary>Selects when a <see cref="GenericDeltaSession"/> re-anchors (non-normative, SPEC 10a.8).</summary>
    public sealed class ReanchorPolicy
    {
        public const int DefaultReanchorN = 15;
        internal bool IsSizeGuard { get; }
        internal int N { get; }
        private ReanchorPolicy(bool sizeGuard, int n) { IsSizeGuard = sizeGuard; N = n; }
        /// <summary>Re-anchor every n turns; n &lt;= 0 falls back to the default cadence.</summary>
        public static ReanchorPolicy FixedN(int n) => new ReanchorPolicy(false, n <= 0 ? DefaultReanchorN : n);
        /// <summary>Re-anchor once cumulative delta bytes reach one full payload. Production-recommended.</summary>
        public static ReanchorPolicy SizeGuard() => new ReanchorPolicy(true, 0);
    }

    internal static class GenericDelta
    {
        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);

        internal sealed class ByteOrderComparer : IComparer<string>
        {
            public static readonly ByteOrderComparer Instance = new ByteOrderComparer();
            public int Compare(string? a, string? b)
            {
                var ab = Encoding.UTF8.GetBytes(a ?? "");
                var bb = Encoding.UTF8.GetBytes(b ?? "");
                int n = Math.Min(ab.Length, bb.Length);
                for (int i = 0; i < n; i++)
                {
                    int x = ab[i], y = bb[i];
                    if (x != y) return x - y;
                }
                return ab.Length - bb.Length;
            }
        }

        internal static string Sha256Hex(string s)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        internal static string CanonicalCell(object? v)
        {
            switch (v)
            {
                case null: return "-";
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return Scalar.FormatNumberValue(d);
                case float f: return Scalar.FormatNumberValue(f);
                case string s: return Scalar.QuoteString(s);
                default: return Scalar.QuoteString(v.ToString() ?? "");
            }
        }

        public static string GenericPackRoot(GenericSet s)
        {
            var sortedFields = s.Fields.OrderBy(x => x, ByteOrderComparer.Instance).ToList();
            var records = s.Rows.Select(row =>
            {
                var r = new StringBuilder("R");
                foreach (var f in sortedFields) r.Append('\t').Append(f).Append('\t').Append(CanonicalCell(row.GetOrNull(f)));
                r.Append('\n');
                return r.ToString();
            }).OrderBy(x => x, ByteOrderComparer.Instance);
            return "sha256:" + Sha256Hex(string.Concat(records));
        }

        private static Dictionary<string, OrderedMap> IndexByKey(GenericSet s)
        {
            var m = new Dictionary<string, OrderedMap>(s.Rows.Count);
            foreach (var row in s.Rows)
            {
                string id = CanonicalCell(row.GetOrNull(s.Key));
                if (m.ContainsKey(id)) throw new DecodeException("delta_invalid: duplicate identity " + id + " for key \"" + s.Key + "\"");
                m[id] = row;
            }
            return m;
        }

        private static string KeyOf(OrderedMap row, string key) => CanonicalCell(row.GetOrNull(key));

        private static bool RowsEqual(OrderedMap a, OrderedMap b, IReadOnlyList<string> fields)
            => fields.All(f => CanonicalCell(a.GetOrNull(f)) == CanonicalCell(b.GetOrNull(f)));

        public static GenericDeltaPayload DiffGenericSets(GenericSet baseSet, GenericSet next)
        {
            if (next.Key.Length == 0) throw new DecodeException("delta_invalid: no identity key");
            if (next.Key != baseSet.Key || !baseSet.Fields.SequenceEqual(next.Fields))
                throw new DecodeException("delta_invalid: schema change (send full)");
            var baseIdx = IndexByKey(baseSet);
            var nextIdx = IndexByKey(next);

            var added = new List<OrderedMap>();
            var changed = new List<OrderedMap>();
            var removed = new List<object?>();

            foreach (var kv in nextIdx)
            {
                if (!baseIdx.TryGetValue(kv.Key, out var brow)) added.Add(kv.Value);
                else if (!RowsEqual(brow, kv.Value, next.Fields)) changed.Add(kv.Value);
            }
            foreach (var kv in baseIdx)
                if (!nextIdx.ContainsKey(kv.Key)) removed.Add(kv.Value.GetOrNull(next.Key));

            return new GenericDeltaPayload
            {
                Key = next.Key,
                Fields = next.Fields.ToList(),
                BaseRoot = GenericPackRoot(baseSet),
                NewRoot = GenericPackRoot(next),
                Added = added.OrderBy(r => KeyOf(r, next.Key), ByteOrderComparer.Instance).ToList(),
                Changed = changed.OrderBy(r => KeyOf(r, next.Key), ByteOrderComparer.Instance).ToList(),
                Removed = removed.OrderBy(CanonicalCell, ByteOrderComparer.Instance).ToList(),
            };
        }

        private static string FieldDecl(IReadOnlyList<string> fields, string key)
            => string.Join(",", fields.Select(f => f == key ? "@" + Scalar.FormatKeyValue(f) : Scalar.FormatKeyValue(f)));

        private static string EncodeRow(OrderedMap row, IReadOnlyList<string> fields)
            => string.Join("|", fields.Select(f => Scalar.FormatScalarValue(row.GetOrNull(f), '|')));

        public static string EncodeGenericFull(GenericSet s, string tool)
        {
            string name = s.Name.Length == 0 ? "rows" : s.Name;
            var b = new StringBuilder("GCF profile=generic");
            if (tool.Length != 0) b.Append(" tool=").Append(tool);
            b.Append(" pack_root=").Append(GenericPackRoot(s)).Append(" key=").Append(s.Key).Append('\n');
            b.Append("## ").Append(name).Append(" [").Append(s.Rows.Count).Append("]{").Append(FieldDecl(s.Fields, s.Key)).Append("}\n");
            foreach (var row in s.Rows) b.Append(EncodeRow(row, s.Fields)).Append('\n');
            return b.ToString();
        }

        public static string EncodeGenericDelta(GenericDeltaPayload d)
        {
            var b = new StringBuilder("GCF profile=generic");
            if (d.Tool.Length != 0) b.Append(" tool=").Append(d.Tool);
            b.Append(" delta=true base_root=").Append(d.BaseRoot).Append(" new_root=").Append(d.NewRoot).Append(" key=").Append(d.Key);
            if (d.FullTokens > 0)
            {
                double savings = 100.0 * (1.0 - (double)d.DeltaTokens / d.FullTokens);
                b.Append(" savings=").Append(Math.Round(savings, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)).Append('%');
            }
            b.Append('\n');

            if (d.Added.Count != 0)
            {
                b.Append("## added [").Append(d.Added.Count).Append("]{").Append(FieldDecl(d.Fields, d.Key)).Append("}\n");
                foreach (var row in d.Added) b.Append(EncodeRow(row, d.Fields)).Append('\n');
            }
            if (d.Changed.Count != 0)
            {
                b.Append("## changed [").Append(d.Changed.Count).Append("]{").Append(FieldDecl(d.Fields, d.Key)).Append("}\n");
                foreach (var row in d.Changed) b.Append(EncodeRow(row, d.Fields)).Append('\n');
            }
            if (d.Removed.Count != 0)
            {
                b.Append("## removed [").Append(d.Removed.Count).Append("]{@").Append(d.Key).Append("}\n");
                foreach (var idv in d.Removed) b.Append(Scalar.FormatScalarValue(idv, '|')).Append('\n');
            }
            return b.ToString();
        }

        public static GenericSet VerifyGenericDelta(GenericSet baseSet, GenericDeltaPayload d, string expectedNewRoot)
        {
            if (GenericPackRoot(baseSet) != d.BaseRoot) throw new DecodeException("base_mismatch: base root does not equal delta base_root");
            var baseIdx = IndexByKey(baseSet);

            foreach (var idv in d.Removed)
                if (!baseIdx.ContainsKey(CanonicalCell(idv))) throw new DecodeException("delta_invalid: removing identity " + CanonicalCell(idv) + " not in base");
            foreach (var row in d.Added)
                if (baseIdx.ContainsKey(KeyOf(row, d.Key))) throw new DecodeException("delta_invalid: adding identity " + KeyOf(row, d.Key) + " that already exists");
            foreach (var row in d.Changed)
                if (!baseIdx.ContainsKey(KeyOf(row, d.Key))) throw new DecodeException("delta_invalid: changing identity " + KeyOf(row, d.Key) + " not in base");

            var work = new Dictionary<string, OrderedMap>(baseIdx);
            var order = new List<string>();
            foreach (var kv in baseIdx) order.Add(kv.Key);
            foreach (var idv in d.Removed) { work.Remove(CanonicalCell(idv)); order.Remove(CanonicalCell(idv)); }
            foreach (var row in d.Added) { var k = KeyOf(row, d.Key); work[k] = row; order.Add(k); }
            foreach (var row in d.Changed) { work[KeyOf(row, d.Key)] = row; }

            var rows = order.Select(k => work[k]).ToList();
            var result = new GenericSet(baseSet.Key, baseSet.Fields, rows, baseSet.Name);
            string got = GenericPackRoot(result);
            if (got != expectedNewRoot) throw new DecodeException("root_mismatch: computed " + got + ", expected " + expectedNewRoot);
            return result;
        }

        // --- consumer-side wire parsing ---

        private static object? ScalarToAny(Scalar.ScalarParsed r)
        {
            switch (r.Kind)
            {
                case Scalar.ScalarKind.Null: return null;
                case Scalar.ScalarKind.Bool: return r.Value;
                case Scalar.ScalarKind.Int: return r.Value;
                case Scalar.ScalarKind.Double: return r.Value;
                case Scalar.ScalarKind.String: return r.Value;
                default: throw new DecodeException("delta_invalid: non-scalar cell not allowed in delta row");
            }
        }

        private static Dictionary<string, string> ParseHeaderFields(string header)
        {
            var m = new Dictionary<string, string>();
            foreach (var tok in WsRe.Split(header))
            {
                int i = tok.IndexOf('=');
                if (i > 0) m[tok.Substring(0, i)] = tok.Substring(i + 1);
            }
            return m;
        }

        private static int ParseCount(string s)
        {
            if (s == "0") return 0;
            if (s.Length == 0 || s[0] == '0') throw new DecodeException("delta_invalid: invalid count " + s);
            if (!int.TryParse(s, out int n)) throw new DecodeException("delta_invalid: invalid count " + s);
            if (n.ToString() != s) throw new DecodeException("delta_invalid: invalid count " + s);
            return n;
        }

        private static int FindBracketStart(string s)
        {
            bool inQuote = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inQuote) { escaped = true; continue; }
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == '[' && !inQuote) return i;
            }
            return -1;
        }

        private static (List<string> fields, string keyField) SplitDeltaFieldDecl(string decl)
        {
            if (decl.Length < 2 || decl[0] != '{' || decl[decl.Length - 1] != '}')
                throw new DecodeException("delta_invalid: invalid field declaration: " + decl);
            string inner = decl.Substring(1, decl.Length - 2);
            if (inner.Length == 0) return (new List<string>(), "");
            var fields = new List<string>();
            string keyField = "";
            foreach (var raw in Scalar.SplitRespectingQuotes(inner, ','))
            {
                string f = raw.Trim();
                bool isKey = false;
                if (f.StartsWith("@", StringComparison.Ordinal)) { f = f.Substring(1); isKey = true; }
                if (f.Length >= 2 && f[0] == '"' && f[f.Length - 1] == '"') f = Scalar.ParseQuotedStringValue(f);
                if (isKey) keyField = f;
                fields.Add(f);
            }
            return (fields, keyField);
        }

        private sealed class SectionHeader
        {
            public string Name; public int Count; public List<string> Fields; public string KeyField;
            public SectionHeader(string name, int count, List<string> fields, string keyField) { Name = name; Count = count; Fields = fields; KeyField = keyField; }
        }

        private static SectionHeader ParseSectionHeader(string content)
        {
            int bi = FindBracketStart(content);
            if (bi < 0) throw new DecodeException("delta_invalid: section header without count: " + content);
            string name = content.Substring(0, bi).Trim();
            string rest = content.Substring(bi);
            if (rest.Length == 0 || rest[0] != '[') throw new DecodeException("delta_invalid: malformed section header: " + content);
            int close = rest.IndexOf(']');
            if (close < 0) throw new DecodeException("delta_invalid: unterminated count: " + content);
            int count = ParseCount(rest.Substring(1, close - 1));
            var (fields, keyField) = SplitDeltaFieldDecl(rest.Substring(close + 1));
            return new SectionHeader(name, count, fields, keyField);
        }

        private static OrderedMap ParseRow(string line, List<string> fields)
        {
            var cells = Scalar.SplitRespectingQuotes(line, '|');
            if (cells.Count != fields.Count) throw new DecodeException("delta_invalid: row has " + cells.Count + " cells, expected " + fields.Count + ": " + line);
            var row = new OrderedMap();
            for (int i = 0; i < fields.Count; i++) row[fields[i]] = ScalarToAny(Scalar.ParseScalarValue(cells[i], true));
            return row;
        }

        public static (GenericSet set, string packRoot) DecodeGenericFull(string text)
        {
            var lines = text.TrimEnd('\n').Split('\n');
            var hdr = ParseHeaderFields(lines[0]);
            if (!hdr.TryGetValue("profile", out var prof) || prof != "generic") throw new DecodeException("not a generic payload");

            string name = "";
            string key = hdr.TryGetValue("key", out var k) ? k : "";
            List<string> fields = new List<string>();
            var rows = new List<OrderedMap>();
            int i = 1;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (!line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (line == "" || line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("##! ", StringComparison.Ordinal)) { i++; continue; }
                    throw new DecodeException("count_mismatch: unexpected content after declared section rows: \"" + line + "\"");
                }
                var sh = ParseSectionHeader(line.Substring(3));
                name = sh.Name;
                fields = sh.Fields;
                if (key.Length == 0) key = sh.KeyField;
                i++;
                for (int j = 0; j < sh.Count; j++)
                {
                    if (i >= lines.Length || lines[i].StartsWith("## ", StringComparison.Ordinal))
                        throw new DecodeException("count_mismatch: declared " + sh.Count + " rows, got " + j);
                    rows.Add(ParseRow(lines[i], fields));
                    i++;
                }
            }
            return (new GenericSet(key, fields, rows, name), hdr.TryGetValue("pack_root", out var pr) ? pr : "");
        }

        public static GenericDeltaPayload DecodeGenericDelta(string text)
        {
            var lines = text.TrimEnd('\n').Split('\n');
            var hdr = ParseHeaderFields(lines[0]);
            if (!hdr.TryGetValue("profile", out var prof) || prof != "generic") throw new DecodeException("not a generic payload");
            if (!hdr.TryGetValue("delta", out var dl) || dl != "true") throw new DecodeException("not a delta payload");

            string key = hdr.TryGetValue("key", out var k) ? k : "";
            List<string> fields = new List<string>();
            bool fieldsSet = false;
            List<OrderedMap> added = new List<OrderedMap>();
            List<OrderedMap> changed = new List<OrderedMap>();
            var removed = new List<object?>();

            int i = 1;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (!line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (line == "" || line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("##! ", StringComparison.Ordinal)) { i++; continue; }
                    throw new DecodeException("count_mismatch: unexpected content after declared section rows: \"" + line + "\"");
                }
                var sh = ParseSectionHeader(line.Substring(3));
                if (key.Length == 0 && sh.KeyField.Length != 0) key = sh.KeyField;
                if (!fieldsSet && (sh.Name == "added" || sh.Name == "changed")) { fields = sh.Fields; fieldsSet = true; }
                i++;
                if (sh.Name == "added" || sh.Name == "changed")
                {
                    var rows = new List<OrderedMap>();
                    for (int j = 0; j < sh.Count; j++)
                    {
                        if (i >= lines.Length || lines[i].StartsWith("## ", StringComparison.Ordinal))
                            throw new DecodeException("count_mismatch: declared " + sh.Count + " rows in ## " + sh.Name + ", got " + j);
                        rows.Add(ParseRow(lines[i], sh.Fields));
                        i++;
                    }
                    if (sh.Name == "added") added = rows; else changed = rows;
                }
                else if (sh.Name == "removed")
                {
                    for (int j = 0; j < sh.Count; j++)
                    {
                        if (i >= lines.Length || lines[i].StartsWith("## ", StringComparison.Ordinal))
                            throw new DecodeException("count_mismatch: declared " + sh.Count + " identities in ## removed, got " + j);
                        removed.Add(ScalarToAny(Scalar.ParseScalarValue(lines[i], true)));
                        i++;
                    }
                }
                else throw new DecodeException("delta_invalid: unknown delta section " + sh.Name);
            }
            return new GenericDeltaPayload
            {
                Key = key,
                Fields = fields,
                BaseRoot = hdr.TryGetValue("base_root", out var br) ? br : "",
                NewRoot = hdr.TryGetValue("new_root", out var nr) ? nr : "",
                Added = added,
                Changed = changed,
                Removed = removed,
                Tool = hdr.TryGetValue("tool", out var t) ? t : "",
            };
        }

        internal static int ByteLen(string s) => Encoding.UTF8.GetByteCount(s);
    }

    /// <summary>
    /// Producer-side helper that manages the re-anchor cadence for a stream of
    /// generic-profile updates (SPEC 10a.8, non-normative). Each Next emits either a
    /// compact delta or, on cadence, a full re-anchor; every payload is byte-identical
    /// to calling EncodeGenericFull / EncodeGenericDelta directly. Not thread-safe.
    /// </summary>
    public sealed class GenericDeltaSession
    {
        private GenericSet _base;
        private readonly string _tool;
        private readonly ReanchorPolicy _policy;
        private int _cum;

        /// <summary>Number of Next calls so far (the initial full is turn 0).</summary>
        public int Turn { get; private set; }

        public GenericDeltaSession(GenericSet baseSet, string tool, ReanchorPolicy policy)
        {
            _base = baseSet; _tool = tool; _policy = policy;
        }

        /// <summary>Full payload for the current base; send first to establish the base.</summary>
        public string CurrentFull() => GenericDelta.EncodeGenericFull(_base, _tool);

        /// <summary>Advance to next; returns (wire, isFullReanchor).</summary>
        public (string wire, bool isFull) Next(GenericSet next)
        {
            Turn++;
            if (next.Key != _base.Key || !_base.Fields.SequenceEqual(next.Fields))
                return (Reanchor(next), true);

            var d = GenericDelta.DiffGenericSets(_base, next);
            var deltaWire = GenericDelta.EncodeGenericDelta(d);

            bool doReanchor = _policy.IsSizeGuard
                ? _cum + GenericDelta.ByteLen(deltaWire) >= GenericDelta.ByteLen(GenericDelta.EncodeGenericFull(next, _tool))
                : Turn % _policy.N == 0;

            if (doReanchor) return (Reanchor(next), true);
            _base = next;
            _cum += GenericDelta.ByteLen(deltaWire);
            return (deltaWire, false);
        }

        private string Reanchor(GenericSet next)
        {
            var wire = GenericDelta.EncodeGenericFull(next, _tool);
            _base = next;
            _cum = 0;
            return wire;
        }
    }
}
