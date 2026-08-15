using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlackwellSystems.Gcf
{
    internal static class DecodeGenericImpl
    {
        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);

        public static object? DecodeGeneric(string input)
        {
            string trimmed = input.TrimEnd('\n', '\r');
            if (trimmed.Length == 0) throw new DecodeException("missing_header: empty input");

            var lines = trimmed.Split('\n');
            string header = lines[0].TrimEnd('\r');
            if (!header.StartsWith("GCF ", StringComparison.Ordinal)) throw new DecodeException("missing_header: first line does not begin with GCF");

            string profile = ParseHeaderProfile(header);
            if (profile == "graph")
            {
                var p = GraphCodec.Decode(input);
                return PayloadToMap(p);
            }
            if (profile != "generic") throw new DecodeException("unknown_profile: " + profile);

            var contentLines = new List<string>();
            string summaryLine = "";
            int deferredCount = 0;
            for (int li = 1; li < lines.Length; li++)
            {
                string l = lines[li].TrimEnd('\r');
                if (l.Length == 0) continue;
                foreach (char c in l) { if (c == '\t') throw new DecodeException("tab_indentation: tabs in leading whitespace"); if (c != ' ') break; }
                string t = l.TrimStart();
                if (t.StartsWith("# ", StringComparison.Ordinal)) continue;
                if (t.StartsWith("##! ", StringComparison.Ordinal)) { summaryLine = t; continue; }
                if (t.StartsWith("## ", StringComparison.Ordinal) && t.Contains("[?]")) deferredCount++;
                contentLines.Add(l);
            }

            if (summaryLine.Length != 0 && deferredCount > 0)
                ValidateSummaryCounts(summaryLine, deferredCount, contentLines);

            if (contentLines.Count == 0) return new OrderedMap();

            string first = contentLines[0].TrimStart();
            if (first.StartsWith("=", StringComparison.Ordinal))
            {
                if (contentLines.Count > 1) throw new DecodeException("trailing_characters: extra lines after root scalar");
                return ScalarToAny(Scalar.ParseScalarValue(first.Substring(1)));
            }
            if (first.StartsWith("## [", StringComparison.Ordinal))
            {
                var (arr, consumed) = ParseArrayFromHeader(contentLines, 0, 0, first.Substring(3));
                if (consumed < contentLines.Count)
                    throw new DecodeException("count_mismatch: declared count is fewer than the rows present");
                return arr;
            }

            var result = new OrderedMap();
            ParseObjectBody(contentLines, 0, 0, result);
            return result;
        }

        private static string ParseHeaderProfile(string header)
        {
            var parts = WsRe.Split(header);
            if (parts.Length < 2) throw new DecodeException("missing_profile");
            var seen = new HashSet<string>();
            string profile = "";
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                int eq = p.IndexOf('=');
                if (eq < 0) throw new DecodeException("malformed_header_field: " + p);
                string key = p.Substring(0, eq);
                if (seen.Contains(key)) throw new DecodeException("duplicate_header_field: " + key);
                seen.Add(key);
                if (key == "profile") profile = p.Substring(eq + 1);
            }
            if (profile.Length == 0) throw new DecodeException("missing_profile");
            return profile;
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
                case Scalar.ScalarKind.Missing: throw new DecodeException("invalid_missing");
                case Scalar.ScalarKind.Attachment: throw new DecodeException("invalid_attachment_marker");
                default: throw new DecodeException("invalid_inline_attachment_marker");
            }
        }

        private static int ParseObjectBody(List<string> lines, int start, int depth, OrderedMap outMap)
        {
            string ind = new string(' ', depth * 2);
            int i = start;
            while (i < lines.Count)
            {
                string line = lines[i];
                if (depth > 0 && !line.StartsWith(ind, StringComparison.Ordinal)) break;
                string content = depth > 0 ? line.Substring(ind.Length) : line;
                if (content.Length != 0 && content[0] == ' ') throw new DecodeException("invalid_indent: indentation increases by more than one level");

                if (content.StartsWith("## ", StringComparison.Ordinal))
                {
                    string hdr = content.Substring(3);
                    int bi = FindHeaderBracketStart(hdr);
                    if (bi >= 0)
                    {
                        string name = ParseKeyFromHeader(hdr.Substring(0, bi));
                        CheckDup(outMap, name);
                        var (arr, consumed) = ParseArrayFromHeader(lines, i, depth, hdr.Substring(bi));
                        outMap[name] = arr;
                        i += consumed; continue;
                    }
                    string name2 = ParseKeyFromHeader(hdr);
                    CheckDup(outMap, name2);
                    i++;
                    var nested = new OrderedMap();
                    int c2 = ParseObjectBody(lines, i, depth + 1, nested);
                    outMap[name2] = nested;
                    i += c2; continue;
                }

                int? eqIdx = FindKVSplit(content);
                if (eqIdx != null && eqIdx > 0)
                {
                    string name = ParseKeyFromHeader(content.Substring(0, eqIdx.Value));
                    CheckDup(outMap, name);
                    outMap[name] = ScalarToAny(Scalar.ParseScalarValue(content.Substring(eqIdx.Value + 1)));
                    i++; continue;
                }

                if (!content.StartsWith("@", StringComparison.Ordinal) && !content.StartsWith("##", StringComparison.Ordinal))
                {
                    int bracketIdx = ArrayBracketStart(content);
                    if (bracketIdx > 0)
                    {
                        string rest = content.Substring(bracketIdx);
                        int closeIdx = rest.IndexOf(']');
                        if (closeIdx >= 0)
                        {
                            string after = rest.Substring(closeIdx + 1);
                            if (after.StartsWith(": ", StringComparison.Ordinal) || after == ":")
                            {
                                string name = ParseKeyFromHeader(content.Substring(0, bracketIdx));
                                CheckDup(outMap, name);
                                var (arr, _) = ParseArrayFromHeader(lines, i, depth, rest);
                                outMap[name] = arr;
                                i++; continue;
                            }
                        }
                    }
                }

                if (content.Contains("|")) throw new DecodeException("orphan_inline_attachment: " + content);
                throw new DecodeException("invalid_line: unexpected content in object body: " + content);
            }
            return i - start;
        }

        // Index of the '[' that opens a named-array marker (name[N]:), scanning past a
        // quoted key so a '[' inside the key name is not mistaken for the array bracket
        // (SPEC 4.2). Bare keys cannot contain '['. Returns -1 when not found.
        private static int ArrayBracketStart(string content)
        {
            if (content.Length > 0 && content[0] == '"')
            {
                bool escaped = false;
                for (int i = 1; i < content.Length; i++)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (content[i] == '\\')
                    {
                        escaped = true;
                    }
                    else if (content[i] == '"')
                    {
                        return (i + 1 < content.Length && content[i + 1] == '[') ? i + 1 : -1;
                    }
                }
                return -1;
            }
            return content.IndexOf('[');
        }

        private static int FindHeaderBracketStart(string s)
        {
            bool inQuote = false;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && inQuote) { i += 2; continue; }
                if (c == '"') { inQuote = !inQuote; i++; continue; }
                if (!inQuote && c == ' ' && i + 1 < s.Length && s[i + 1] == '[') return i;
                i++;
            }
            return -1;
        }

        private static int? FindKVSplit(string s)
        {
            if (s.Length == 0) return null;
            if (s[0] == '"')
            {
                int i = 1;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') return (i + 1 < s.Length && s[i + 1] == '=') ? i + 1 : (int?)null;
                    i++;
                }
                return null;
            }
            int eqIdx = s.IndexOf('=');
            if (eqIdx < 0) return null;
            int bracketIdx = s.IndexOf('[');
            if (bracketIdx >= 0 && bracketIdx < eqIdx) return null;
            return eqIdx;
        }

        private static string ParseKeyFromHeader(string s)
        {
            string trimmed = s.Trim();
            return (trimmed.Length >= 2 && trimmed[0] == '"') ? Scalar.ParseQuotedStringValue(trimmed) : trimmed;
        }

        private static void CheckDup(OrderedMap map, string key)
        {
            if (map.ContainsKey(key)) throw new DecodeException("duplicate_key: " + key);
        }

        private static (object value, int consumed) ParseArrayFromHeader(List<string> lines, int headerLine, int depth, string bracketPart)
        {
            string bp = bracketPart.TrimStart();
            if (!bp.StartsWith("[", StringComparison.Ordinal)) throw new DecodeException("invalid_count: " + bp);
            int close = bp.IndexOf(']');
            if (close < 0) throw new DecodeException("invalid_count: " + bp);
            string countStr = bp.Substring(1, close - 1);
            string after = bp.Substring(close + 1);

            bool keyed = countStr.EndsWith(":", StringComparison.Ordinal);
            if (keyed)
            {
                countStr = countStr.Substring(0, countStr.Length - 1);
                if (!after.StartsWith("{", StringComparison.Ordinal)) throw new DecodeException("keyed_map: missing field declaration");
            }

            int count = countStr == "?" ? -1 : ParseCountVal(countStr);

            if (keyed && count == 0) throw new DecodeException("keyed_map: zero count [0:] is invalid (an empty object uses Section 7.7)");

            if (count == 0 && !after.StartsWith("{", StringComparison.Ordinal) && !after.StartsWith(":", StringComparison.Ordinal))
                return (new List<object?>(), 1);

            if (after.StartsWith(": ", StringComparison.Ordinal) || after == ":")
            {
                string valsStr = after.StartsWith(": ", StringComparison.Ordinal) ? after.Substring(2) : "";
                if (valsStr.Length == 0)
                {
                    if (count >= 0 && count != 0) throw new DecodeException("count_mismatch: declared " + count + ", got 0");
                    return (new List<object?>(), 1);
                }
                var vals = Scalar.SplitRespectingQuotes(valsStr, ',');
                if (count >= 0 && vals.Count != count) throw new DecodeException("count_mismatch: declared " + count + ", got " + vals.Count);
                var outList = new List<object?>();
                foreach (var v in vals) outList.Add(ScalarToAny(Scalar.ParseScalarValue(v.Trim())));
                return (outList, 1);
            }

            if (after.StartsWith("{", StringComparison.Ordinal))
            {
                int? braceEnd = Scalar.FindClosingBraceIdx(after);
                if (braceEnd == null) throw new DecodeException("invalid field declaration");
                var fields = Scalar.SplitFieldDeclValue(after.Substring(0, braceEnd.Value + 1));
                if (keyed && fields.Count < 2) throw new DecodeException("keyed_map: header must declare at least two fields");
                var (rows, consumed) = ParseTabularBody(lines, headerLine + 1, depth, fields, count);
                if (count >= 0 && rows.Count != count) throw new DecodeException("count_mismatch: declared " + count + ", got " + rows.Count);
                if (keyed) return (KeyedRowsToMap(rows, fields), consumed + 1);
                return (rows, consumed + 1);
            }

            var (items, c) = ParseExpandedBody(lines, headerLine + 1, depth);
            if (count >= 0 && items.Count != count) throw new DecodeException("count_mismatch: declared " + count + ", got " + items.Count);
            return (items, c + 1);
        }

        private static OrderedMap KeyedRowsToMap(List<object> rows, List<string> fields)
        {
            if (fields.Count < 2) throw new DecodeException("keyed_map: header must declare at least two fields");
            string keyLabel = fields[0];
            var outMap = new OrderedMap();
            foreach (var r in rows)
            {
                if (!(r is OrderedMap row)) throw new DecodeException("keyed_map: row is not an object");
                if (!row.ContainsKey(keyLabel)) throw new DecodeException("keyed_map: row missing key column \"" + keyLabel + "\"");
                var kv = row[keyLabel];
                string ks = kv as string ?? (kv?.ToString() ?? "");
                if (outMap.ContainsKey(ks)) throw new DecodeException("keyed_map: duplicate member key \"" + ks + "\"");
                var value = new OrderedMap();
                foreach (var kvp in row) if (kvp.Key != keyLabel) value[kvp.Key] = kvp.Value;
                outMap[ks] = value;
            }
            return outMap;
        }

        private static (string name, string after) ParseAttachmentName(string rest)
        {
            if (rest.Length != 0 && rest[0] == '"')
            {
                int j = 1;
                while (j < rest.Length)
                {
                    if (rest[j] == '\\') { j += 2; continue; }
                    if (rest[j] == '"')
                    {
                        string name = Scalar.ParseQuotedStringValue(rest.Substring(0, j + 1));
                        return (name, rest.Substring(j + 1));
                    }
                    j++;
                }
                return ("", rest);
            }
            int sp = rest.IndexOf(' ');
            return sp >= 0 ? (rest.Substring(0, sp), rest.Substring(sp)) : (rest, "");
        }

        private sealed class AttachmentResult
        {
            public string Name; public object? Value; public int Consumed; public List<string>? ParsedFields;
            public AttachmentResult(string name, object? value, int consumed, List<string>? parsedFields)
            { Name = name; Value = value; Consumed = consumed; ParsedFields = parsedFields; }
        }

        private static AttachmentResult ParseAttachment(List<string> lines, int lineIdx, string rest, int depth, Dictionary<string, List<string>> sharedSchemas)
        {
            var (name, afterNameRaw) = ParseAttachmentName(rest);
            if (name.Length == 0 && !rest.StartsWith("\"\"", StringComparison.Ordinal)) throw new DecodeException("invalid attachment: " + rest);
            string afterName = afterNameRaw.TrimStart();

            if (afterName.StartsWith("{}", StringComparison.Ordinal))
            {
                var nested = new OrderedMap();
                int consumed = ParseObjectBody(lines, lineIdx + 1, depth, nested);
                return new AttachmentResult(name, nested, consumed + 1, null);
            }
            if (afterName.StartsWith("[", StringComparison.Ordinal))
            {
                int cb = afterName.IndexOf(']');
                if (cb < 0) throw new DecodeException("invalid_count: missing ]");
                string afterClose = afterName.Substring(cb + 1);

                if (afterClose.StartsWith("{", StringComparison.Ordinal))
                {
                    int? endBrace = Scalar.FindClosingBraceIdx(afterClose);
                    List<string>? parsedFields = null;
                    if (endBrace != null)
                    {
                        try { parsedFields = Scalar.SplitFieldDeclValue(afterClose.Substring(0, endBrace.Value + 1)); } catch { }
                    }
                    var (arr, consumed) = ParseArrayFromHeader(lines, lineIdx, depth, afterName);
                    return new AttachmentResult(name, arr, consumed, parsedFields);
                }

                if (afterClose.StartsWith(": ", StringComparison.Ordinal) || afterClose == ":")
                {
                    var (arr, consumed) = ParseArrayFromHeader(lines, lineIdx, depth, afterName);
                    return new AttachmentResult(name, arr, consumed, null);
                }

                if (sharedSchemas.TryGetValue(name, out var sf))
                {
                    string countStr = afterName.Substring(1, cb - 1);
                    int count = countStr == "?" ? -1 : int.Parse(countStr);
                    if (count == 0) return new AttachmentResult(name, new List<object?>(), 1, null);
                    bool useShared = true;
                    int nextIdx = lineIdx + 1;
                    string ind = new string(' ', depth * 2);
                    if (nextIdx < lines.Count)
                    {
                        string nc = lines[nextIdx];
                        if (depth > 0 && nc.StartsWith(ind, StringComparison.Ordinal)) nc = nc.Substring(ind.Length);
                        if (nc.TrimStart().StartsWith("@", StringComparison.Ordinal)) useShared = false;
                    }
                    if (useShared)
                    {
                        var (rows, consumed) = ParseTabularBody(lines, lineIdx + 1, depth, sf, count);
                        if (count >= 0 && rows.Count != count) throw new DecodeException("count_mismatch: declared " + count + ", got " + rows.Count);
                        return new AttachmentResult(name, rows, consumed + 1, null);
                    }
                }

                var (arr2, consumed2) = ParseArrayFromHeader(lines, lineIdx, depth, afterName);
                return new AttachmentResult(name, arr2, consumed2, null);
            }
            if (afterName.StartsWith("=", StringComparison.Ordinal))
            {
                string valStr = afterName.Substring(1);
                var parsed = Scalar.ParseScalarValue(valStr, tabularContext: true);
                if (parsed.Kind == Scalar.ScalarKind.Missing) return new AttachmentResult(name, null, 1, null);
                return new AttachmentResult(name, ScalarToAny(parsed), 1, null);
            }
            throw new DecodeException("invalid attachment form: " + afterName);
        }

        private static (List<object> rows, int consumed) ParseTabularBody(List<string> lines, int start, int depth, List<string> fields, int expectedCount)
        {
            string ind = new string(' ', depth * 2);
            var rows = new List<object>();
            int i = start;

            var inlineSchemas = new Dictionary<string, List<string>>();
            var sharedArraySchemas = new Dictionary<string, List<string>>();

            var pathColumnMap = new Dictionary<string, List<string>>();
            foreach (var f in fields)
            {
                if (f.Contains(">"))
                {
                    var parts = f.Split('>').ToList();
                    if (parts.All(p => p.Length != 0)) pathColumnMap[f] = parts;
                }
            }

            while (i < lines.Count)
            {
                string line = lines[i];
                string content;
                if (depth > 0) { if (!line.StartsWith(ind, StringComparison.Ordinal)) break; content = line.Substring(ind.Length); }
                else content = line;
                if (content.StartsWith("## ", StringComparison.Ordinal) || content.StartsWith("##!", StringComparison.Ordinal)) break;
                if (content.Length != 0 && content[0] == ' ') break;

                string rowData = content;
                bool rowHasID = false;
                if (rowData.StartsWith("@", StringComparison.Ordinal))
                {
                    int sp = rowData.IndexOf(' ');
                    if (sp > 0)
                    {
                        string idStr = rowData.Substring(1, sp - 1);
                        if (idStr.Length != 0 && idStr.All(char.IsDigit)) { rowData = rowData.Substring(sp + 1); rowHasID = true; }
                    }
                }

                var vals = Scalar.SplitRespectingQuotes(rowData, '|');
                if (vals.Count != fields.Count) throw new DecodeException("row_width_mismatch: expected " + fields.Count + ", got " + vals.Count);

                var cellValues = new OrderedMap();
                var traditionalAttFields = new List<string>();
                var inlineAttFields = new List<string>();
                var inlineAttOrder = new List<string>();
                var missingFields = new HashSet<string>();
                var flatValues = new Dictionary<string, object?>();
                var flatAbsent = new HashSet<string>();

                for (int j = 0; j < fields.Count; j++)
                {
                    string f = fields[j];
                    string cellVal = vals[j];

                    if (pathColumnMap.ContainsKey(f))
                    {
                        var parsed0 = Scalar.ParseScalarValue(cellVal, tabularContext: true);
                        if (parsed0.Kind == Scalar.ScalarKind.Missing) flatAbsent.Add(f);
                        else flatValues[f] = ScalarToAny(parsed0);
                        continue;
                    }

                    if (cellVal.StartsWith("^{", StringComparison.Ordinal) && cellVal.EndsWith("}", StringComparison.Ordinal))
                    {
                        var ifs = Scalar.SplitFieldDeclValue(cellVal.Substring(1));
                        inlineSchemas[f] = ifs;
                        inlineAttFields.Add(f);
                        inlineAttOrder.Add(f);
                        continue;
                    }
                    var parsed = Scalar.ParseScalarValue(cellVal, tabularContext: true);
                    switch (parsed.Kind)
                    {
                        case Scalar.ScalarKind.Missing: missingFields.Add(f); break;
                        case Scalar.ScalarKind.Attachment:
                            if (inlineSchemas.ContainsKey(f)) { inlineAttFields.Add(f); inlineAttOrder.Add(f); }
                            else traditionalAttFields.Add(f);
                            break;
                        case Scalar.ScalarKind.InlineAttachment:
                            {
                                var ifs2 = Scalar.SplitFieldDeclValue(parsed.Schema!);
                                inlineSchemas[f] = ifs2;
                                inlineAttFields.Add(f);
                                inlineAttOrder.Add(f);
                                break;
                            }
                        default: cellValues[f] = ScalarToAny(parsed); break;
                    }
                }
                i++;

                var allAttFields = traditionalAttFields.Concat(inlineAttFields).ToList();
                var attachmentValues = new OrderedMap();

                if (rowHasID)
                {
                    var resolvedAttachments = new HashSet<string>();
                    int inlineIdx = 0;
                    var expectedAtt = new HashSet<string>(traditionalAttFields);
                    foreach (var f in inlineAttFields) expectedAtt.Add(f);

                    while (i < lines.Count)
                    {
                        string aLine = lines[i];
                        string? aContent;
                        if (depth == 0 || aLine.StartsWith(ind, StringComparison.Ordinal)) aContent = depth > 0 ? aLine.Substring(ind.Length) : aLine;
                        else aContent = null;
                        if (aContent == null) break;

                        if (!aContent.StartsWith(".", StringComparison.Ordinal) && aContent.StartsWith("  .", StringComparison.Ordinal)) aContent = aContent.Substring(2);

                        if (aContent.StartsWith(".", StringComparison.Ordinal))
                        {
                            string rest = aContent.Substring(1);
                            var (attName, afterNameR) = ParseAttachmentName(rest);
                            string afterNameS = afterNameR.TrimStart();

                            if (!expectedAtt.Contains(attName) && !attName.Contains(">")) throw new DecodeException("orphan_attachment: " + attName);
                            if (resolvedAttachments.Contains(attName)) throw new DecodeException("duplicate_attachment: " + attName);

                            if (inlineSchemas.TryGetValue(attName, out var ifs) && !afterNameS.StartsWith("{}", StringComparison.Ordinal) && !afterNameS.StartsWith("[", StringComparison.Ordinal))
                            {
                                var inlineVals = Scalar.SplitRespectingQuotes(afterNameS, '|');
                                if (inlineVals.Count != ifs.Count) throw new DecodeException("inline_width_mismatch: " + attName + " expected " + ifs.Count + ", got " + inlineVals.Count);
                                var obj = new OrderedMap();
                                for (int k = 0; k < ifs.Count; k++)
                                {
                                    var pp = Scalar.ParseScalarValue(inlineVals[k], tabularContext: true);
                                    if (pp.Kind != Scalar.ScalarKind.Missing) obj[ifs[k]] = ScalarToAny(pp);
                                }
                                attachmentValues[attName] = obj;
                                resolvedAttachments.Add(attName);
                                i++; continue;
                            }

                            var result = ParseAttachment(lines, i, rest, depth + 2, sharedArraySchemas);
                            if (rows.Count == 0 && result.ParsedFields != null) sharedArraySchemas[result.Name] = result.ParsedFields;
                            attachmentValues[result.Name] = result.Value;
                            resolvedAttachments.Add(result.Name);
                            i += result.Consumed; continue;
                        }

                        bool foundInline = false;
                        string nextInlineField = "";
                        while (inlineIdx < inlineAttOrder.Count)
                        {
                            string candidate = inlineAttOrder[inlineIdx];
                            if (!attachmentValues.ContainsKey(candidate)) { nextInlineField = candidate; foundInline = true; break; }
                            inlineIdx++;
                        }
                        if (!foundInline) break;

                        var ifs3 = inlineSchemas[nextInlineField];
                        var inlineVals3 = Scalar.SplitRespectingQuotes(aContent, '|');
                        if (inlineVals3.Count != ifs3.Count) throw new DecodeException("inline_width_mismatch: " + nextInlineField + " expected " + ifs3.Count + ", got " + inlineVals3.Count);
                        var obj3 = new OrderedMap();
                        for (int k = 0; k < ifs3.Count; k++)
                        {
                            var pp = Scalar.ParseScalarValue(inlineVals3[k], tabularContext: true);
                            if (pp.Kind != Scalar.ScalarKind.Missing) obj3[ifs3[k]] = ScalarToAny(pp);
                        }
                        attachmentValues[nextInlineField] = obj3;
                        inlineIdx++; i++;
                    }

                    if (i < lines.Count)
                    {
                        string extraLine = lines[i];
                        string extraContent = "";
                        if (depth == 0 || extraLine.StartsWith(ind, StringComparison.Ordinal)) extraContent = depth > 0 ? extraLine.Substring(ind.Length) : extraLine;
                        if (!extraContent.StartsWith(".", StringComparison.Ordinal) && extraContent.StartsWith("  .", StringComparison.Ordinal)) extraContent = extraContent.Substring(2);
                        if (extraContent.StartsWith(".", StringComparison.Ordinal))
                        {
                            var (extraName, _) = ParseAttachmentName(extraContent.Substring(1));
                            if (resolvedAttachments.Contains(extraName)) throw new DecodeException("duplicate_attachment: " + extraName);
                        }
                    }

                    foreach (var f in allAttFields)
                        if (!attachmentValues.ContainsKey(f)) throw new DecodeException("missing_attachment: " + f);
                }

                if (!rowHasID || allAttFields.Count == 0)
                {
                    string attIndent = ind + "  ";
                    if (i < lines.Count && lines[i].StartsWith(attIndent, StringComparison.Ordinal))
                    {
                        string peek = lines[i].Substring(attIndent.Length);
                        if (peek.StartsWith(".", StringComparison.Ordinal)) throw new DecodeException("orphan_attachment: " + peek);
                    }
                }

                var nested = pathColumnMap.Count != 0 ? UnflattenPaths(pathColumnMap, flatValues, flatAbsent) : new OrderedMap();
                var emittedGroups = new HashSet<string>();
                var row = new OrderedMap();
                foreach (var f in fields)
                {
                    if (pathColumnMap.ContainsKey(f))
                    {
                        string top = pathColumnMap[f][0];
                        if (emittedGroups.Contains(top)) continue;
                        emittedGroups.Add(top);
                        if (nested.ContainsKey(top)) row[top] = nested[top];
                        continue;
                    }
                    if (missingFields.Contains(f)) continue;
                    if (cellValues.ContainsKey(f)) { row[f] = cellValues[f]; continue; }
                    if (attachmentValues.ContainsKey(f)) { row[f] = attachmentValues[f]; continue; }
                }
                foreach (var kvp in attachmentValues)
                    if (!row.ContainsKey(kvp.Key)) row[kvp.Key] = kvp.Value;

                rows.Add(row);

                if (expectedCount >= 0 && rows.Count >= expectedCount) break;
            }
            return (rows, i - start);
        }

        private static OrderedMap UnflattenPaths(Dictionary<string, List<string>> pathColumns, Dictionary<string, object?> flatValues, HashSet<string> flatAbsent)
        {
            var groups = new OrderedMap();
            var groupLists = new Dictionary<string, List<string>>();
            var groupOrder = new List<string>();
            foreach (var kvp in pathColumns)
            {
                var paths = kvp.Value;
                if (paths.Count == 0) continue;
                string top = paths[0];
                if (!groupLists.ContainsKey(top)) { groupLists[top] = new List<string>(); groupOrder.Add(top); }
                groupLists[top].Add(kvp.Key);
            }

            var result = new OrderedMap();
            foreach (var top in groupOrder)
            {
                var fieldNames = groupLists[top];
                bool allAbsent = fieldNames.All(f => flatAbsent.Contains(f));
                bool allNull = fieldNames.All(f => flatAbsent.Contains(f) ? false : (flatValues.TryGetValue(f, out var vv) ? vv == null : true));

                if (allAbsent) continue;
                if (allNull) { result[top] = null; continue; }

                foreach (var fieldName in fieldNames)
                {
                    if (flatAbsent.Contains(fieldName)) continue;
                    if (!pathColumns.TryGetValue(fieldName, out var paths)) continue;
                    object? value = flatValues.TryGetValue(fieldName, out var vv2) ? vv2 : null;

                    OrderedMap current = result;
                    for (int k = 0; k < paths.Count - 1; k++)
                    {
                        string key = paths[k];
                        if (!current.ContainsKey(key) || !(current[key] is OrderedMap)) current[key] = new OrderedMap();
                        current = (OrderedMap)current[key]!;
                    }
                    current[paths[paths.Count - 1]] = value;
                }
            }
            _ = groups;
            return result;
        }

        private static (List<object?> items, int consumed) ParseExpandedBody(List<string> lines, int start, int depth)
        {
            string ind = new string(' ', depth * 2);
            var items = new List<object?>();
            int i = start;

            while (i < lines.Count)
            {
                string line = lines[i];
                string content;
                if (depth > 0) { if (!line.StartsWith(ind, StringComparison.Ordinal)) break; content = line.Substring(ind.Length); }
                else content = line;
                if (content.StartsWith("## ", StringComparison.Ordinal) || content.StartsWith("##!", StringComparison.Ordinal)) break;
                if (!content.StartsWith("@", StringComparison.Ordinal)) break;
                int sp = content.IndexOf(' ');
                if (sp < 0) break;

                string idStr = content.Substring(1, sp - 1);
                if (int.TryParse(idStr, out int id) && id != items.Count) throw new DecodeException("invalid_item_id: expected @" + items.Count + ", got @" + idStr);

                string marker = content.Substring(sp + 1);
                if (marker.StartsWith("=", StringComparison.Ordinal))
                {
                    items.Add(ScalarToAny(Scalar.ParseScalarValue(marker.Substring(1))));
                    i++; continue;
                }
                if (marker.StartsWith("{}", StringComparison.Ordinal))
                {
                    var nested = new OrderedMap();
                    i++;
                    int consumed = ParseObjectBody(lines, i, depth + 1, nested);
                    items.Add(nested);
                    i += consumed; continue;
                }
                if (marker.StartsWith("[", StringComparison.Ordinal))
                {
                    var (arr, consumed) = ParseArrayFromHeader(lines, i, depth + 1, marker);
                    items.Add(arr);
                    i += consumed; continue;
                }
                break;
            }
            return (items, i - start);
        }

        private static int ParseCountVal(string s)
        {
            if (s == "0") return 0;
            if (s.Length == 0 || s[0] == '0') throw new DecodeException("invalid_count: " + s);
            if (!int.TryParse(s, out int n)) throw new DecodeException("invalid_count: " + s);
            if (n.ToString() != s) throw new DecodeException("invalid_count: " + s);
            return n;
        }

        internal static OrderedMap PayloadToMap(Payload p)
        {
            var m = new OrderedMap();
            m["tool"] = p.Tool;
            m["tokenBudget"] = (long)p.TokenBudget;
            m["tokensUsed"] = (long)p.TokensUsed;
            m["packRoot"] = p.PackRoot;
            var syms = new List<object?>();
            foreach (var s in p.Symbols)
            {
                var sm = new OrderedMap();
                sm["qualifiedName"] = s.QualifiedName; sm["kind"] = s.Kind; sm["score"] = s.Score;
                sm["provenance"] = s.Provenance; sm["distance"] = (long)s.Distance;
                syms.Add(sm);
            }
            m["symbols"] = syms;
            var edges = new List<object?>();
            foreach (var e in p.Edges)
            {
                var em = new OrderedMap();
                em["source"] = e.Source; em["target"] = e.Target; em["edgeType"] = e.EdgeType; em["status"] = e.Status;
                edges.Add(em);
            }
            m["edges"] = edges;
            return m;
        }

        private static void ValidateSummaryCounts(string summaryLine, int deferredCount, List<string> contentLines)
        {
            string? countsStr = WsRe.Split(summaryLine).FirstOrDefault(x => x.StartsWith("counts=", StringComparison.Ordinal))?.Substring(7);
            if (countsStr == null) return;
            var countVals = countsStr.Split(',');
            if (countVals.Length != deferredCount) throw new DecodeException("count_mismatch: summary has " + countVals.Length + " count entries but " + deferredCount + " deferred sections");

            var actualCounts = new List<int>();
            bool inDeferred = false;
            int currentCount = 0;
            foreach (var line in contentLines)
            {
                string t = line.TrimStart();
                if (t.StartsWith("## ", StringComparison.Ordinal) && t.Contains("[?]")) { if (inDeferred) actualCounts.Add(currentCount); inDeferred = true; currentCount = 0; continue; }
                if (t.StartsWith("## ", StringComparison.Ordinal)) { if (inDeferred) { actualCounts.Add(currentCount); inDeferred = false; } continue; }
                if (inDeferred && !t.StartsWith(" ", StringComparison.Ordinal) && !t.StartsWith(".", StringComparison.Ordinal)) currentCount++;
            }
            if (inDeferred) actualCounts.Add(currentCount);
            for (int idx = 0; idx < countVals.Length; idx++)
            {
                if (!int.TryParse(countVals[idx], out int declared)) throw new DecodeException("count_mismatch: invalid count value '" + countVals[idx] + "'");
                if (idx < actualCounts.Count && declared != actualCounts[idx]) throw new DecodeException("count_mismatch: section " + idx + " declared " + declared + " in summary, actual " + actualCounts[idx]);
            }
        }
    }
}
