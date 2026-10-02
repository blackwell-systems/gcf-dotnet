using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackwellSystems.Gcf
{
    // This file implements the v3.6.0 tabular column optimizations for the generic
    // profile: constant-column factoring (SPEC 7.4.7) and value-grouping (SPEC 7.4.8).
    // Constant-column factoring is mandatory canonical and rides the default encoder
    // (EncodeTabular, Generic.cs); the decode side and the opt-in grouped encoder are
    // here.
    internal static class ConstantGrouping
    {
        // FieldEntry is one parsed entry of a tabular field declaration. A plain field has
        // only a name. A constant column (SPEC 7.4.7) carries an unparsed value token after
        // an unquoted "=". A key column (SPEC 7.4.8.1, 10a.1) carries a leading "@".
        internal sealed class FieldEntry
        {
            public string Name = "";
            public bool IsKey;
            public bool IsConst;
            public string ConstTok = "";
        }

        // QuotedStringEnd returns the index just past the closing quote of a quoted string
        // that starts at s[0], or -1 if unterminated.
        private static int QuotedStringEnd(string s)
        {
            bool escaped = false;
            for (int i = 1; i < s.Length; i++)
            {
                if (escaped) { escaped = false; continue; }
                if (s[i] == '\\') { escaped = true; continue; }
                if (s[i] == '"') return i + 1;
            }
            return -1;
        }

        // SplitNameValue parses a field entry's name and optional "=value" tail. The name is
        // a Section 2a key (bare or quoted); the "=" that introduces a constant value is the
        // first unquoted "=" after the (possibly quoted) name. A null value means the entry
        // is a plain field (no "=").
        private static (string name, string? value) SplitNameValue(string r)
        {
            if (r.Length == 0) throw new DecodeException("malformed_header_field: empty field entry");
            if (r[0] == '"')
            {
                int end = QuotedStringEnd(r);
                if (end < 0) throw new DecodeException("unterminated_quote: field name");
                string nm = Scalar.ParseQuotedStringValue(r.Substring(0, end));
                string after = r.Substring(end);
                if (after.Length == 0) return (nm, null);
                if (after[0] == '=') return (nm, after.Substring(1));
                throw new DecodeException("malformed_header_field: unexpected characters after quoted field name");
            }
            int idx = r.IndexOf('=');
            if (idx >= 0)
            {
                string nm = r.Substring(0, idx);
                if (nm.Length == 0) throw new DecodeException("malformed_header_field: empty field name");
                if (!IsBareKey(nm)) throw new DecodeException("invalid field name: " + nm);
                return (nm, r.Substring(idx + 1));
            }
            if (!IsBareKey(r)) throw new DecodeException("invalid field name: " + r);
            return (r, null);
        }

        private static bool IsBareKey(string s)
        {
            if (s.Length == 0) return false;
            char c = s[0];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_')) return false;
            for (int i = 1; i < s.Length; i++)
            {
                c = s[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }

        // ParseFieldEntries parses a {...} field declaration supporting "@" key markers and
        // "name=value" constant columns. Commas, and the "=" boundary, are parsed respecting
        // quoted names and quoted values (SPEC 7.4.7.2, mirroring 2a.3).
        internal static List<FieldEntry> ParseFieldEntries(string declStr)
        {
            if (declStr.Length < 2 || declStr[0] != '{' || declStr[declStr.Length - 1] != '}')
                throw new DecodeException("invalid field declaration: " + declStr);
            string inner = declStr.Substring(1, declStr.Length - 2);
            if (inner.Length == 0) return new List<FieldEntry>();
            var raw = Scalar.SplitRespectingQuotes(inner, ',');
            var entries = new List<FieldEntry>(raw.Count);
            foreach (var rawEntry in raw)
            {
                string r = rawEntry.Trim();
                var e = new FieldEntry();
                if (r.StartsWith("@", StringComparison.Ordinal)) { e.IsKey = true; r = r.Substring(1); }
                var (nm, val) = SplitNameValue(r);
                e.Name = nm;
                if (val != null) { e.IsConst = true; e.ConstTok = val; }
                entries.Add(e);
            }
            var seen = new HashSet<string>();
            foreach (var e in entries)
            {
                if (seen.Contains(e.Name)) throw new DecodeException("duplicate_field_name: " + e.Name);
                seen.Add(e.Name);
            }
            return entries;
        }

        // ParseConstValue parses a constant-column value token into a scalar (SPEC 7.4.7.2).
        // The absent marker and empty/attachment tokens are rejected.
        private static object? ParseConstValue(string tok)
        {
            if (tok.Length == 0) throw new DecodeException("invalid_const_value: empty constant value (the empty string is always quoted)");
            if (tok == "~") throw new DecodeException("invalid_const_value: absent marker ~ is not valid in a field declaration");
            // Reject only a complete attachment marker, mirroring the encoder's Section 2.4
            // quoting predicate (Scalar: bare "^", or "^{...}" ending in "}"). A "^{"-prefixed
            // token without a closing "}" (e.g. "^{abc") is not a marker; it is a literal string,
            // and the encoder leaves it bare, so the decoder must accept it as a scalar.
            if (tok == "^" || (tok.Length >= 3 && tok[0] == '^' && tok[1] == '{' && tok[tok.Length - 1] == '}'))
                throw new DecodeException("invalid_const_value: attachment marker is not a scalar");
            var parsed = Scalar.ParseScalarValue(tok, tabularContext: false);
            return ScalarToAny(parsed);
        }

        private static object? ScalarToAny(Scalar.ScalarParsed sv)
        {
            switch (sv.Kind)
            {
                case Scalar.ScalarKind.Null: return null;
                case Scalar.ScalarKind.Bool: return sv.Value;
                case Scalar.ScalarKind.Int: return sv.Value;
                case Scalar.ScalarKind.Double: return sv.Value;
                case Scalar.ScalarKind.String: return sv.Value;
                default: throw new DecodeException("invalid_const_value: unexpected token kind");
            }
        }

        // FormatConstValue formats a scalar as a constant-column header value (SPEC 7.4.7.2):
        // the Section 2.4 obligation plus quoting when the value contains "}" (the "," case is
        // already covered by needsQuote). Null is "-".
        internal static string FormatConstValue(object? v)
        {
            if (v == null) return "-";
            if (v is string s)
            {
                if (Scalar.NeedsQuote(s) || s.IndexOf('}') >= 0) return Scalar.QuoteString(s);
                return s;
            }
            return Scalar.FormatScalarValue(v, '\0');
        }

        // PathTopLevel returns the top-level group key of a flattened path column (SPEC
        // 7.4.6) and true when the name is a valid path (contains ">" with all segments
        // non-empty), mirroring parseTabularBody's path-column detection.
        private static bool PathTopLevel(string name, out string top)
        {
            top = "";
            if (!name.Contains(">")) return false;
            var parts = name.Split('>');
            foreach (var p in parts) if (p.Length == 0) return false;
            top = parts[0];
            return true;
        }

        // DecodeConstantArray parses a tabular array whose field declaration contains one or
        // more constant columns (SPEC 7.4.7). It parses the rows with the bare (per-record)
        // fields only, then rebuilds each record in declaration order, inserting each constant
        // at its position. headerLine is the header's line index; it returns the records and
        // the number of lines consumed including the header.
        internal static (List<object> records, int consumed) DecodeConstantArray(
            List<string> lines, int headerLine, int depth, List<FieldEntry> entries, int count,
            Func<List<string>, int, int, List<string>, int, (List<object> rows, int consumed)> parseTabularBody)
        {
            var bareFields = new List<string>();
            var constVals = new Dictionary<string, object?>();
            foreach (var e in entries)
            {
                if (e.IsConst) { constVals[e.Name] = ParseConstValue(e.ConstTok); continue; }
                bareFields.Add(e.Name);
            }
            if (bareFields.Count == 0)
                throw new DecodeException("no_bare_column: every field is constant; a row must carry at least one per-record column");
            var (rows, consumed) = parseTabularBody(lines, headerLine + 1, depth, bareFields, count);
            if (count >= 0 && rows.Count != count)
                throw new DecodeException("count_mismatch: declared " + count + ", got " + rows.Count);

            // Plan the output-key order over all entries, mirroring parseTabularBody: a bare
            // path column (contains ">") collapses to its top-level key at the first occurrence,
            // a plain field keeps its name, and a constant contributes its name at its position.
            var planNames = new List<string>();
            var planConst = new List<bool>();
            var inPlan = new HashSet<string>();
            var seenGroup = new HashSet<string>();
            foreach (var e in entries)
            {
                if (e.IsConst) { planNames.Add(e.Name); planConst.Add(true); inPlan.Add(e.Name); continue; }
                if (PathTopLevel(e.Name, out var top))
                {
                    if (!seenGroup.Contains(top)) { seenGroup.Add(top); planNames.Add(top); planConst.Add(false); inPlan.Add(top); }
                    continue;
                }
                planNames.Add(e.Name); planConst.Add(false); inPlan.Add(e.Name);
            }

            var outList = new List<object>(rows.Count);
            foreach (var r in rows)
            {
                var rm = r as OrderedMap;
                var nm = new OrderedMap();
                for (int k = 0; k < planNames.Count; k++)
                {
                    if (planConst[k]) { nm[planNames[k]] = constVals[planNames[k]]; continue; }
                    if (rm != null && rm.ContainsKey(planNames[k])) nm[planNames[k]] = rm[planNames[k]];
                }
                // Append any keys the record carries that were not in the plan (flatten-fallback
                // attachments, Section 7.4.6.1.4), in the record's own order.
                if (rm != null)
                    foreach (var kv in rm)
                        if (!inPlan.Contains(kv.Key)) nm[kv.Key] = kv.Value;
                outList.Add(nm);
            }
            return (outList, consumed + 1);
        }

        // ParseHeaderKey parses a Section 2a key (bare or quoted) that occupies the whole of s.
        private static string ParseHeaderKey(string s)
        {
            if (s.Length == 0) throw new DecodeException("empty key");
            if (s[0] == '"')
            {
                int end = QuotedStringEnd(s);
                if (end != s.Length) throw new DecodeException("malformed quoted key: " + s);
                return Scalar.ParseQuotedStringValue(s);
            }
            if (!IsBareKey(s)) throw new DecodeException("invalid key: " + s);
            return s;
        }

        // IndexUnquotedEq returns the index of the first "=" outside a quoted string, or -1.
        private static int IndexUnquotedEq(string s)
        {
            bool inQuote = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inQuote) { escaped = true; continue; }
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == '=' && !inQuote) return i;
            }
            return -1;
        }

        // ParseGroupSubheader parses a line of the form `{col}={value} [{count}]` (SPEC
        // 7.4.8.3). The value runs from the first unquoted "=" to the final " [" that begins
        // the count.
        private static (string col, object? value, int count) ParseGroupSubheader(string content)
        {
            if (!content.EndsWith("]", StringComparison.Ordinal))
                throw new DecodeException("invalid_group_header: subheader missing count bracket");
            int cntOpen = content.LastIndexOf(" [", StringComparison.Ordinal);
            if (cntOpen < 0) throw new DecodeException("invalid_group_header: subheader missing count bracket");
            string countStr = content.Substring(cntOpen + 2, content.Length - 1 - (cntOpen + 2));
            int n = ParseCount(countStr);
            if (n == 0) throw new DecodeException("invalid_count: a group names at least one record");
            string colEqVal = content.Substring(0, cntOpen);
            int eq = IndexUnquotedEq(colEqVal);
            if (eq < 0) throw new DecodeException("invalid_group_header: subheader missing '='");
            string colName = ParseHeaderKeyWrapped(colEqVal.Substring(0, eq));
            string valTok = colEqVal.Substring(eq + 1);
            object? v = ParseConstValue(valTok);
            return (colName, v, n);
        }

        private static string ParseHeaderKeyWrapped(string s)
        {
            try { return ParseHeaderKey(s); }
            catch (DecodeException ex) { throw new DecodeException("invalid_group_header: " + ex.Message); }
        }

        private static int ParseCount(string s)
        {
            if (s == "0") return 0;
            if (s.Length == 0 || s[0] == '0') throw new DecodeException("invalid_count: " + s);
            int n = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') throw new DecodeException("invalid_count: " + s);
                n = n * 10 + (c - '0');
            }
            return n;
        }

        // DecodeGroupedArray parses a value-grouped tabular array (SPEC 7.4.8). groupClause is
        // the trimmed text after the field declaration's "}" (beginning with "group=").
        internal static (List<object> records, int consumed) DecodeGroupedArray(
            List<string> lines, int headerLine, int depth, List<FieldEntry> entries, string groupClause, int count)
        {
            if (!groupClause.StartsWith("group=", StringComparison.Ordinal))
                throw new DecodeException("invalid_group_header: malformed group clause");
            string groupCol;
            try { groupCol = ParseHeaderKey(groupClause.Substring("group=".Length).Trim()); }
            catch (DecodeException ex) { throw new DecodeException("invalid_group_header: " + ex.Message); }

            int keyCount = 0;
            string keyName = "";
            foreach (var e in entries) if (e.IsKey) { keyCount++; keyName = e.Name; }
            if (keyCount != 1)
                throw new DecodeException("invalid_group_header: a grouped section requires exactly one @ key column");

            // Validate the grouping column: present, not the key, not a constant column.
            FieldEntry? groupEntry = null;
            foreach (var e in entries) if (e.Name == groupCol) { groupEntry = e; break; }
            if (groupEntry == null)
                throw new DecodeException("invalid_group_header: group column \"" + groupCol + "\" is not a declared field");
            if (groupEntry.IsKey)
                throw new DecodeException("invalid_group_header: group column \"" + groupCol + "\" is the key column");
            if (groupEntry.IsConst)
                throw new DecodeException("invalid_group_header: group column \"" + groupCol + "\" is a constant column");

            // Per-record (bare) fields are the non-constant fields other than the grouping
            // column; the key column is included.
            var bareFields = new List<string>();
            var constVals = new Dictionary<string, object?>();
            foreach (var e in entries)
            {
                if (e.IsConst) { constVals[e.Name] = ParseConstValue(e.ConstTok); continue; }
                if (e.Name == groupCol) continue;
                bareFields.Add(e.Name);
            }

            string indent = new string(' ', depth * 2);
            var records = new List<object>();
            var seenGroups = new HashSet<string>();
            var seenKeys = new HashSet<string>();
            int total = 0;
            int i = headerLine + 1;
            while (i < lines.Count)
            {
                string content = lines[i];
                if (depth > 0)
                {
                    if (!content.StartsWith(indent, StringComparison.Ordinal)) break;
                    content = content.Substring(indent.Length);
                }
                if (content.StartsWith("## ", StringComparison.Ordinal) || content.StartsWith("##!", StringComparison.Ordinal)) break;

                // Subheader: {col}={value} [{count}]
                var (col, groupVal, gcount) = ParseGroupSubheader(content);
                if (col != groupCol)
                    throw new DecodeException("invalid_group_header: subheader column \"" + col + "\" does not match group column \"" + groupCol + "\"");
                string gkey = Scalar.FormatScalarValue(groupVal, '\0');
                if (seenGroups.Contains(gkey)) throw new DecodeException("duplicate_group: " + gkey);
                seenGroups.Add(gkey);
                i++;

                for (int n = 0; n < gcount; n++)
                {
                    if (i >= lines.Count)
                        throw new DecodeException("count_mismatch: group \"" + gkey + "\" declared " + gcount + " rows, found fewer");
                    string rowContent = lines[i];
                    if (depth > 0)
                    {
                        if (!rowContent.StartsWith(indent, StringComparison.Ordinal))
                            throw new DecodeException("count_mismatch: group \"" + gkey + "\" declared " + gcount + " rows, found fewer");
                        rowContent = rowContent.Substring(indent.Length);
                    }
                    if (rowContent.StartsWith("## ", StringComparison.Ordinal) || rowContent.StartsWith("##!", StringComparison.Ordinal))
                        throw new DecodeException("count_mismatch: group \"" + gkey + "\" declared " + gcount + " rows, found fewer");
                    var cells = Scalar.SplitRespectingQuotes(rowContent, '|');
                    if (cells.Count != bareFields.Count)
                        throw new DecodeException("row_width_mismatch: expected " + bareFields.Count + " fields, got " + cells.Count);
                    var bareVals = new Dictionary<string, object?>();
                    for (int j = 0; j < bareFields.Count; j++)
                    {
                        string cell = cells[j];
                        // Only a complete attachment marker (bare "^" or "^{...}" ending in "}")
                        // is forbidden here; a "^{"-prefixed cell without a closing "}" is a
                        // literal scalar (Section 7.4 row cell), not an attachment.
                        if (cell == "^" || (cell.Length >= 3 && cell[0] == '^' && cell[1] == '{' && cell[cell.Length - 1] == '}'))
                            throw new DecodeException("invalid_group_header: grouped records must not carry attachments");
                        var pv = Scalar.ParseScalarValue(cell, tabularContext: true);
                        if (pv.Kind == Scalar.ScalarKind.Missing) continue;
                        bareVals[bareFields[j]] = ScalarToAny(pv);
                    }
                    var nm = new OrderedMap();
                    foreach (var e in entries)
                    {
                        if (e.Name == groupCol) nm[e.Name] = groupVal;
                        else if (e.IsConst) nm[e.Name] = constVals[e.Name];
                        else if (bareVals.ContainsKey(e.Name)) nm[e.Name] = bareVals[e.Name];
                    }
                    if (!nm.ContainsKey(keyName))
                        throw new DecodeException("invalid_group_header: record missing key column \"" + keyName + "\"");
                    string ks = Scalar.FormatScalarValue(nm[keyName], '\0');
                    if (seenKeys.Contains(ks)) throw new DecodeException("duplicate_key: " + ks);
                    seenKeys.Add(ks);
                    records.Add(nm);
                    i++;
                }
                total += gcount;
            }

            if (count >= 0 && total != count)
                throw new DecodeException("count_mismatch: declared " + count + ", got " + total);
            return (records, i - headerLine);
        }

        // --- native model helpers (mirror Generic.cs) ---
        private static bool IsMap(object? v) => v is OrderedMap;
        private static bool IsList(object? v) => v is IList && !(v is string);
        private static OrderedMap AsMap(object? v) => (OrderedMap)v!;

        private static List<string>? TabularFields(IList arr)
        {
            if (arr.Count == 0) return null;
            var fieldOrder = new List<string>();
            var seen = new HashSet<string>();
            foreach (var item in arr)
            {
                if (!IsMap(item)) return null;
                foreach (var k in AsMap(item).Keys)
                    if (seen.Add(k)) fieldOrder.Add(k);
            }
            return fieldOrder.Count == 0 ? null : fieldOrder;
        }

        private static string FormatKey(string s) => Scalar.FormatKeyValue(s);

        /// <summary>
        /// Encode an array of uniform records as a value-grouped keyed set (SPEC 7.4.8):
        /// opt-in, never the canonical default. keyField is the unique identity column
        /// (emitted @-marked); groupField is the low-cardinality column the records are
        /// clustered by. Other constant columns are factored (SPEC 7.4.7). It throws an
        /// EncodeException when the array is not a keyed set the grammar can represent:
        /// a missing key/group field, a non-unique key, key == group, or any record needing
        /// an attachment (nested value), which grouped rows do not carry in this version.
        /// </summary>
        internal static string EncodeGenericGrouped(object? data, string keyField, string groupField)
        {
            if (!(data is IList arr) || data is string)
                throw new EncodeException("value-grouping requires a JSON array");
            if (arr.Count == 0)
                throw new EncodeException("value-grouping requires a non-empty array");
            if (keyField == groupField)
                throw new EncodeException("value-grouping: key field and group field must differ");
            foreach (var item in arr)
                if (!IsMap(item)) throw new EncodeException("value-grouping requires an array of objects");

            var fields = TabularFields(arr);
            if (fields == null)
                throw new EncodeException("value-grouping requires an array of objects with fields");
            if (!fields.Contains(keyField))
                throw new EncodeException("value-grouping: key field \"" + keyField + "\" not present in the records");
            if (!fields.Contains(groupField))
                throw new EncodeException("value-grouping: group field \"" + groupField + "\" not present in the records");

            var keySeen = new HashSet<string>();
            foreach (var item in arr)
            {
                var map = AsMap(item);
                foreach (var f in fields)
                {
                    if (!map.ContainsKey(f)) continue;
                    var val = map[f];
                    if (IsMap(val) || IsList(val))
                        throw new EncodeException("value-grouping does not support nested values in this version: field \"" + f + "\"");
                }
                if (!map.ContainsKey(keyField) || map[keyField] == null)
                    throw new EncodeException("value-grouping: key field \"" + keyField + "\" missing in a record");
                string ks = Scalar.FormatScalarValue(map[keyField], '\0');
                if (keySeen.Contains(ks))
                    throw new EncodeException("value-grouping: key field \"" + keyField + "\" is not unique (" + ks + ")");
                keySeen.Add(ks);
            }

            // Constant columns (excluding key and group), factored per SPEC 7.4.7.
            var constVal = new Dictionary<string, string>();
            if (arr.Count >= 2)
            {
                foreach (var f in fields)
                {
                    if (f == keyField || f == groupField) continue;
                    string first = "";
                    bool firstSet = false;
                    bool isc = true;
                    foreach (var item in arr)
                    {
                        var map = AsMap(item);
                        if (!map.ContainsKey(f)) { isc = false; break; }
                        string cv = FormatConstValue(map[f]);
                        if (!firstSet) { first = cv; firstSet = true; }
                        else if (cv != first) { isc = false; break; }
                    }
                    if (isc) constVal[f] = first;
                }
            }

            var headerFields = new List<string>();
            foreach (var f in fields)
            {
                if (f == keyField) headerFields.Add("@" + FormatKey(f));
                else if (f == groupField) headerFields.Add(FormatKey(f));
                else if (constVal.TryGetValue(f, out var cv)) headerFields.Add(FormatKey(f) + "=" + cv);
                else headerFields.Add(FormatKey(f));
            }

            var bareFields = new List<string>();
            foreach (var f in fields)
            {
                if (f == groupField) continue;
                if (constVal.ContainsKey(f)) continue;
                bareFields.Add(f);
            }

            var groupOrder = new List<string>();
            var groupMembers = new Dictionary<string, List<OrderedMap>>();
            var groupValRaw = new Dictionary<string, object?>();
            foreach (var item in arr)
            {
                var map = AsMap(item);
                object? gv = map.ContainsKey(groupField) ? map[groupField] : null;
                string gk = Scalar.FormatScalarValue(gv, '\0');
                if (!groupMembers.ContainsKey(gk)) { groupOrder.Add(gk); groupValRaw[gk] = gv; groupMembers[gk] = new List<OrderedMap>(); }
                groupMembers[gk].Add(map);
            }

            var b = new StringBuilder();
            b.Append("GCF profile=generic\n");
            b.Append("## [").Append(arr.Count).Append("]{").Append(string.Join(",", headerFields))
             .Append("} group=").Append(FormatKey(groupField)).Append("\n");
            foreach (var gk in groupOrder)
            {
                var members = groupMembers[gk];
                string gvStr = Scalar.FormatScalarValue(groupValRaw[gk], '\0');
                b.Append(FormatKey(groupField)).Append("=").Append(gvStr).Append(" [").Append(members.Count).Append("]\n");
                foreach (var item in members)
                {
                    var cells = new string[bareFields.Count];
                    for (int j = 0; j < bareFields.Count; j++)
                    {
                        string f = bareFields[j];
                        if (!item.ContainsKey(f)) cells[j] = "~";
                        else if (item[f] == null) cells[j] = "-";
                        else cells[j] = Scalar.FormatScalarValue(item[f], '|');
                    }
                    b.Append(string.Join("|", cells)).Append("\n");
                }
            }
            return b.ToString();
        }
    }
}
