using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackwellSystems.Gcf
{
    /// <summary>Options controlling generic encoding behavior.</summary>
    public sealed class GenericOptions
    {
        /// <summary>
        /// When true, disables promotion of fixed-shape nested objects to path
        /// columns (e.g. "customer&gt;name"). Nested objects use attachment syntax instead.
        /// </summary>
        public bool NoFlatten { get; set; }
    }

    internal static class Generic
    {
        // --- native model helpers ---
        internal static bool IsMap(object? v) => v is OrderedMap;
        internal static bool IsList(object? v) => v is IList && !(v is string);
        internal static OrderedMap AsMap(object? v) => (OrderedMap)v!;
        internal static IList AsList(object? v) => (IList)v!;

        private static string Indent(int depth) => new string(' ', depth * 2);

        private static bool AllPrimitives(IList arr)
        {
            foreach (var it in arr) if (IsMap(it) || IsList(it)) return false;
            return true;
        }

        public static string EncodeGeneric(object? data, GenericOptions? opts = null)
        {
            opts ??= new GenericOptions();
            var outb = new StringBuilder("GCF profile=generic\n");
            EncodeRootValue(data, outb, opts);
            return outb.ToString();
        }

        private static void EncodeRootValue(object? v, StringBuilder outb, GenericOptions opts)
        {
            if (v == null) { outb.Append("=-\n"); return; }
            if (IsMap(v))
            {
                var m = AsMap(v);
                var km = KeyedMapEligible(m);
                if (km.Ok) EncodeKeyedMap(null, km, outb, 0, opts);
                else EncodeObject(m, outb, 0, opts);
                return;
            }
            if (IsList(v)) { EncodeRootArray(AsList(v), outb, opts); return; }
            outb.Append("=").Append(Scalar.FormatScalarValue(v)).Append("\n");
        }

        private static void EncodeObject(OrderedMap map, StringBuilder outb, int depth, GenericOptions opts)
        {
            string prefix = Indent(depth);
            foreach (var kv in map)
            {
                string key = kv.Key; object? value = kv.Value;
                string fk = Scalar.FormatKeyValue(key);
                if (IsMap(value))
                {
                    var vm = AsMap(value);
                    var km = KeyedMapEligible(vm);
                    if (km.Ok) EncodeKeyedMap(key, km, outb, depth, opts);
                    else { outb.Append(prefix).Append("## ").Append(fk).Append("\n"); EncodeObject(vm, outb, depth + 1, opts); }
                }
                else if (IsList(value)) EncodeNamedArray(fk, AsList(value), outb, depth, opts);
                else outb.Append(prefix).Append(fk).Append("=").Append(Scalar.FormatScalarValue(value)).Append("\n");
            }
        }

        private static void EncodeRootArray(IList arr, StringBuilder outb, GenericOptions opts)
        {
            if (arr.Count == 0) { outb.Append("## [0]\n"); return; }
            if (AllPrimitives(arr)) { outb.Append("## [").Append(arr.Count).Append("]: ").Append(JoinPrimitives(arr)).Append("\n"); return; }
            var fields = TabularFields(arr);
            if (fields != null) { EncodeTabular("## ", arr, fields, outb, 0, opts); return; }
            EncodeExpanded("## ", arr, outb, 0, opts);
        }

        private static void EncodeNamedArray(string name, IList arr, StringBuilder outb, int depth, GenericOptions opts)
        {
            string prefix = Indent(depth);
            if (arr.Count == 0) { outb.Append(prefix).Append("## ").Append(name).Append(" [0]\n"); return; }
            if (AllPrimitives(arr)) { outb.Append(prefix).Append(name).Append("[").Append(arr.Count).Append("]: ").Append(JoinPrimitives(arr)).Append("\n"); return; }
            var fields = TabularFields(arr);
            if (fields != null) { EncodeTabular(prefix + "## " + name + " ", arr, fields, outb, depth, opts); return; }
            EncodeExpanded(prefix + "## " + name + " ", arr, outb, depth, opts);
        }

        private static string JoinPrimitives(IList arr)
        {
            var parts = new List<string>();
            foreach (var it in arr) parts.Add(Scalar.FormatScalarValue(it, ','));
            return string.Join(",", parts);
        }

        private static List<string>? TabularFields(IList arr)
        {
            if (arr.Count == 0) return null;
            var fieldOrder = new List<string>();
            var seen = new HashSet<string>();
            foreach (var item in arr)
            {
                if (!IsMap(item)) return null;
                foreach (var k in AsMap(item).Keys)
                {
                    if (!seen.Contains(k)) { fieldOrder.Add(k); seen.Add(k); }
                }
            }
            return fieldOrder.Count == 0 ? null : fieldOrder;
        }

        private static List<string>? InlineSchemaFields(IList arr, string fieldName)
        {
            if (arr.Count == 0) return null;
            if (!IsMap(arr[0])) return null;
            var first = AsMap(arr[0]);
            if (!first.ContainsKey(fieldName)) return null;
            if (!IsMap(first[fieldName])) return null;

            List<string>? canonicalKeys = null;
            foreach (var item in arr)
            {
                if (!IsMap(item)) return null;
                var map = AsMap(item);
                if (!map.ContainsKey(fieldName) || map[fieldName] == null) continue;
                var v = map[fieldName];
                if (!IsMap(v)) return null;
                var vm = AsMap(v);
                var keys = vm.Keys.ToList();
                foreach (var value in vm) if (IsMap(value.Value) || IsList(value.Value)) return null;
                if (canonicalKeys == null) canonicalKeys = keys;
                else if (!keys.SequenceEqual(canonicalKeys)) return null;
            }
            return (canonicalKeys != null && canonicalKeys.Count >= 3) ? canonicalKeys : null;
        }

        private static List<string>? SharedArraySchema(IList arr, string fieldName)
        {
            if (arr.Count == 0) return null;
            if (!IsMap(arr[0])) return null;
            var first = AsMap(arr[0]);
            if (!first.ContainsKey(fieldName)) return null;
            if (!IsList(first[fieldName])) return null;

            List<string>? canonicalFields = null;
            foreach (var item in arr)
            {
                if (!IsMap(item)) return null;
                var map = AsMap(item);
                if (!map.ContainsKey(fieldName) || map[fieldName] == null) continue;
                var v = map[fieldName];
                if (!IsList(v)) return null;
                var fields = TabularFields(AsList(v));
                if (fields == null) return null;
                foreach (var arrItem in AsList(v))
                {
                    if (!IsMap(arrItem)) return null;
                    foreach (var value in AsMap(arrItem)) if (IsMap(value.Value) || IsList(value.Value)) return null;
                }
                if (canonicalFields == null) canonicalFields = fields;
                else if (!fields.SequenceEqual(canonicalFields)) return null;
            }
            return canonicalFields;
        }

        // -- Nested object flattening (v3.2) --
        private sealed class FlatLeaf
        {
            public string Path; public List<string> Keys;
            public FlatLeaf(string path, List<string> keys) { Path = path; Keys = keys; }
        }

        private static List<FlatLeaf>? AnalyzeFlattenable(IList arr, string fieldName, string parentPath)
        {
            if (fieldName.Length == 0 || fieldName.Contains(">")) return null;
            Dictionary<string, string>? canonicalShape = null;
            List<string>? canonicalKeys = null;

            foreach (var item in arr)
            {
                if (!IsMap(item)) return null;
                var map = AsMap(item);
                if (!map.ContainsKey(fieldName)) continue;
                var v = map[fieldName];
                if (v == null) { if (parentPath.Length != 0) return null; continue; }
                if (!IsMap(v)) return null;
                var obj = AsMap(v);
                var keys = obj.Keys.ToList();

                if (canonicalShape == null)
                {
                    var shape = new Dictionary<string, string>();
                    foreach (var k in keys)
                    {
                        if (k.Length == 0 || k.Contains(">")) return null;
                        var value = obj[k];
                        if (IsList(value)) return null;
                        else if (IsMap(value)) shape[k] = "nested";
                        else shape[k] = "scalar";
                    }
                    canonicalShape = shape; canonicalKeys = keys;
                }
                else
                {
                    if (!keys.SequenceEqual(canonicalKeys!)) return null;
                    foreach (var k in keys)
                    {
                        if (!canonicalShape.TryGetValue(k, out var expected)) return null;
                        var value = obj[k];
                        if (expected == "scalar" && (IsMap(value) || IsList(value))) return null;
                        if (expected == "nested" && IsList(value)) return null;
                        if (expected == "nested" && value != null && !IsMap(value)) return null;
                    }
                }
            }

            if (canonicalShape == null || canonicalKeys == null) return null;
            var shapeF = canonicalShape; var ck = canonicalKeys;
            string currentPath = parentPath.Length == 0 ? fieldName : parentPath + ">" + fieldName;
            var parentKeys = parentPath.Length == 0 ? new List<string> { fieldName } : parentPath.Split('>').Concat(new[] { fieldName }).ToList();

            var leaves = new List<FlatLeaf>();
            foreach (var k in ck)
            {
                if (shapeF[k] == "scalar")
                {
                    leaves.Add(new FlatLeaf(currentPath + ">" + k, parentKeys.Concat(new[] { k }).ToList()));
                }
                else
                {
                    var subArr = new List<object?>();
                    foreach (var item in arr)
                    {
                        if (!IsMap(item)) { subArr.Add(new OrderedMap()); continue; }
                        var map = AsMap(item);
                        if (!map.ContainsKey(fieldName) || map[fieldName] == null) subArr.Add(new OrderedMap());
                        else subArr.Add(map[fieldName]);
                    }
                    var subLeaves = AnalyzeFlattenable(subArr, k, currentPath);
                    if (subLeaves == null) return null;
                    if (subLeaves.Count == 0) return null;
                    leaves.AddRange(subLeaves);
                }
            }

            if (leaves.Count != 0)
            {
                foreach (var item in arr)
                {
                    if (!IsMap(item)) continue;
                    var map = AsMap(item);
                    if (!map.ContainsKey(fieldName) || map[fieldName] == null) continue;
                    bool allNull = leaves.All(leaf =>
                    {
                        var (value, exists) = ResolveKeyChain(item, leaf.Keys);
                        return exists && value == null;
                    });
                    if (allNull) return null;
                }
            }

            return leaves;
        }

        private static (object? value, bool exists) ResolveKeyChain(object? item, List<string> keys)
        {
            if (keys.Count == 0 || !IsMap(item)) return (null, false);
            var map = AsMap(item);
            if (!map.ContainsKey(keys[0])) return (null, false);
            object? current = map[keys[0]];
            if (current == null) return (null, true);
            for (int i = 1; i < keys.Count; i++)
            {
                if (!IsMap(current)) return (null, false);
                var m = AsMap(current);
                if (!m.ContainsKey(keys[i])) return (null, false);
                current = m[keys[i]];
            }
            return (current, true);
        }

        private sealed class FlatCol
        {
            public string Header, Type, Field; public List<string> Keys;
            public FlatCol(string header, string type, string field, List<string> keys) { Header = header; Type = type; Field = field; Keys = keys; }
        }

        // -- Keyed map (SPEC 7.2a) --
        private sealed class KeyedMap
        {
            public List<string> Keys = new List<string>();
            public List<OrderedMap> Values = new List<OrderedMap>();
            public List<string> ValueFields = new List<string>();
            public string KeyLabel = "";
            public bool Ok;
        }

        private static KeyedMap KeyedMapEligible(OrderedMap m)
        {
            var not = new KeyedMap { Ok = false };
            if (m.Count < 2) return not;

            var keys = m.Keys.ToList();
            var values = new List<OrderedMap>(keys.Count);
            var valueFields = new List<string>();
            var seen = new HashSet<string>();
            foreach (var k in keys)
            {
                var v = m[k];
                if (!IsMap(v)) return not;
                var vo = AsMap(v);
                values.Add(vo);
                foreach (var f in vo.Keys) if (seen.Add(f)) valueFields.Add(f);
            }
            if (valueFields.Count == 0) return not;
            if (!valueFields.Any(f => !f.Contains(">"))) return not;

            string keyLabel = "key";
            while (valueFields.Contains(keyLabel)) keyLabel = "_" + keyLabel;

            return new KeyedMap { Keys = keys, Values = values, ValueFields = valueFields, KeyLabel = keyLabel, Ok = true };
        }

        private static void EncodeKeyedMap(string? name, KeyedMap km, StringBuilder outb, int depth, GenericOptions opts)
        {
            EncodeKeyedMapWithPrefix(KeyedHeaderPrefix(name, depth), km, outb, depth, opts);
        }

        private static string KeyedHeaderPrefix(string? name, int depth)
        {
            string prefix = Indent(depth);
            return name == null ? prefix + "## " : prefix + "## " + Scalar.FormatKeyValue(name) + " ";
        }

        private static void EncodeKeyedMapWithPrefix(string headerPrefix, KeyedMap km, StringBuilder outb, int depth, GenericOptions opts)
        {
            var fields = new List<string>(km.ValueFields.Count + 1) { km.KeyLabel };
            fields.AddRange(km.ValueFields);

            var arr = new List<object?>(km.Keys.Count);
            for (int i = 0; i < km.Keys.Count; i++)
            {
                var aug = new OrderedMap();
                foreach (var kv in km.Values[i]) aug[kv.Key] = kv.Value;
                aug[km.KeyLabel] = km.Keys[i];
                arr.Add(aug);
            }
            EncodeTabular(headerPrefix, arr, fields, outb, depth, opts, keyed: true);
        }

        private sealed class Att
        {
            public string Name; public object? Value; public bool Inline; public List<string>? InlineFields;
            public Att(string name, object? value, bool inline, List<string>? inlineFields) { Name = name; Value = value; Inline = inline; InlineFields = inlineFields; }
        }

        private static void EncodeTabular(string headerPrefix, IList arr, List<string> fields, StringBuilder outb, int depth, GenericOptions opts, bool keyed = false)
        {
            string prefix = Indent(depth);

            var flattenMap = new Dictionary<string, List<FlatLeaf>>();
            if (!opts.NoFlatten)
            {
                foreach (var f in fields)
                {
                    var leaves = AnalyzeFlattenable(arr, f, "");
                    if (leaves != null && leaves.Count != 0) flattenMap[f] = leaves;
                }
            }

            var gtFields = new HashSet<string>(fields.Where(f => !flattenMap.ContainsKey(f) && f.Contains(">")));

            var columns = new List<FlatCol>();
            foreach (var f in fields)
            {
                if (gtFields.Contains(f)) continue;
                if (flattenMap.TryGetValue(f, out var leaves))
                {
                    foreach (var leaf in leaves) columns.Add(new FlatCol(Scalar.FormatKeyValue(leaf.Path), "flat", f, leaf.Keys));
                }
                else columns.Add(new FlatCol(Scalar.FormatKeyValue(f), "original", f, new List<string>()));
            }

            if (columns.Count == 0) { EncodeExpanded(headerPrefix, arr, outb, depth, opts); return; }

            var inlineSchemas = new Dictionary<string, List<string>>();
            var sharedArrSchemas = new Dictionary<string, List<string>>();
            foreach (var f in fields)
            {
                if (flattenMap.ContainsKey(f)) continue;
                var isf = InlineSchemaFields(arr, f); if (isf != null) inlineSchemas[f] = isf;
                var sas = SharedArraySchema(arr, f); if (sas != null) sharedArrSchemas[f] = sas;
            }

            string headerFields = string.Join(",", columns.Select(c => c.Header));
            string br = keyed ? ":]" : "]";
            outb.Append(headerPrefix).Append("[").Append(arr.Count).Append(br).Append("{").Append(headerFields).Append("}\n");

            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (!IsMap(item)) continue;
                var map = AsMap(item);
                var cells = new List<string>();
                var attachments = new List<Att>();
                bool rowHasAttachment = false;

                foreach (var col in columns)
                {
                    if (col.Type == "flat")
                    {
                        if (!map.ContainsKey(col.Keys[0])) { cells.Add("~"); continue; }
                        var topVal = map[col.Keys[0]];
                        if (topVal == null) { cells.Add("-"); continue; }
                        var (value, exists) = ResolveKeyChain(item, col.Keys);
                        if (!exists) cells.Add("~");
                        else if (value == null) cells.Add("-");
                        else cells.Add(Scalar.FormatScalarValue(value, '|'));
                        continue;
                    }

                    var fld = col.Field;
                    if (!map.ContainsKey(fld)) { cells.Add("~"); continue; }
                    var v = map[fld];
                    if (v == null) { cells.Add("-"); continue; }
                    if (IsMap(v) || IsList(v))
                    {
                        if (inlineSchemas.TryGetValue(fld, out var ifs) && IsMap(v))
                        {
                            if (i == 0) cells.Add("^{" + string.Join(",", ifs.Select(Scalar.FormatKeyValue)) + "}");
                            else cells.Add("^");
                            attachments.Add(new Att(fld, v, true, ifs));
                        }
                        else { cells.Add("^"); attachments.Add(new Att(fld, v, false, null)); }
                        rowHasAttachment = true;
                    }
                    else cells.Add(Scalar.FormatScalarValue(v, '|'));
                }

                foreach (var f in fields)
                {
                    if (!gtFields.Contains(f)) continue;
                    if (!map.ContainsKey(f)) continue;
                    rowHasAttachment = true;
                    attachments.Add(new Att(f, map[f], false, null));
                }

                string row = string.Join("|", cells);
                if (rowHasAttachment) outb.Append(prefix).Append("@").Append(i).Append(" ").Append(row).Append("\n");
                else outb.Append(prefix).Append(row).Append("\n");

                foreach (var att in attachments)
                {
                    string fk = Scalar.FormatKeyValue(att.Name);
                    if (att.Inline && att.InlineFields != null)
                    {
                        var obj = AsMap(att.Value);
                        var vals = att.InlineFields.Select(inf => !obj.ContainsKey(inf) ? "~" : Scalar.FormatScalarValue(obj[inf], '|'));
                        outb.Append(prefix).Append(string.Join("|", vals)).Append("\n");
                    }
                    else if (IsMap(att.Value))
                    {
                        var am = AsMap(att.Value);
                        var km2 = KeyedMapEligible(am);
                        if (km2.Ok) EncodeKeyedMapWithPrefix(prefix + "." + fk + " ", km2, outb, depth + 2, opts);
                        else { outb.Append(prefix).Append(".").Append(fk).Append(" {}\n"); EncodeObject(am, outb, depth + 2, opts); }
                    }
                    else if (IsList(att.Value))
                    {
                        if (sharedArrSchemas.TryGetValue(att.Name, out var sas) && i > 0)
                            EncodeAttachmentArrayShared(prefix, fk, AsList(att.Value), outb, depth + 2, sas, opts);
                        else EncodeAttachmentArray(prefix, fk, AsList(att.Value), outb, depth + 2, opts);
                    }
                    else outb.Append(prefix).Append(".").Append(fk).Append(" =").Append(Scalar.FormatScalarValue(att.Value)).Append("\n");
                }
            }
        }

        private static void EncodeAttachmentArrayShared(string attPrefix, string fk, IList arr, StringBuilder outb, int depth, List<string> sharedFields, GenericOptions opts)
        {
            if (arr.Count == 0) { outb.Append(attPrefix).Append(".").Append(fk).Append(" [0]\n"); return; }
            if (AllPrimitives(arr)) { outb.Append(attPrefix).Append(".").Append(fk).Append(" [").Append(arr.Count).Append("]: ").Append(JoinPrimitives(arr)).Append("\n"); return; }
            var fields = TabularFields(arr);
            if (fields != null && fields.SequenceEqual(sharedFields))
            {
                string prefix = Indent(depth);
                outb.Append(attPrefix).Append(".").Append(fk).Append(" [").Append(arr.Count).Append("]\n");
                foreach (var item in arr)
                {
                    if (!IsMap(item)) continue;
                    var map = AsMap(item);
                    var cells = sharedFields.Select(f => !map.ContainsKey(f) ? "~" : map[f] == null ? "-" : Scalar.FormatScalarValue(map[f], '|'));
                    outb.Append(prefix).Append(string.Join("|", cells)).Append("\n");
                }
            }
            else EncodeAttachmentArray(attPrefix, fk, arr, outb, depth, opts);
        }

        private static void EncodeAttachmentArray(string attPrefix, string fk, IList arr, StringBuilder outb, int depth, GenericOptions opts)
        {
            if (arr.Count == 0) { outb.Append(attPrefix).Append(".").Append(fk).Append(" [0]\n"); return; }
            if (AllPrimitives(arr)) { outb.Append(attPrefix).Append(".").Append(fk).Append(" [").Append(arr.Count).Append("]: ").Append(JoinPrimitives(arr)).Append("\n"); return; }
            var fields = TabularFields(arr);
            if (fields != null) { EncodeTabular(attPrefix + "." + fk + " ", arr, fields, outb, depth, opts); return; }
            EncodeExpanded(attPrefix + "." + fk + " ", arr, outb, depth, opts);
        }

        private static void EncodeExpanded(string headerPrefix, IList arr, StringBuilder outb, int depth, GenericOptions opts)
        {
            string prefix = Indent(depth);
            outb.Append(headerPrefix).Append("[").Append(arr.Count).Append("]\n");
            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (IsMap(item))
                {
                    var im = AsMap(item);
                    var km = KeyedMapEligible(im);
                    if (km.Ok) EncodeKeyedMapWithPrefix(prefix + "@" + i + " ", km, outb, depth + 1, opts);
                    else { outb.Append(prefix).Append("@").Append(i).Append(" {}\n"); EncodeObject(im, outb, depth + 1, opts); }
                }
                else if (IsList(item)) EncodeExpandedArrayItem(prefix, i, AsList(item), outb, depth, opts);
                else outb.Append(prefix).Append("@").Append(i).Append(" =").Append(Scalar.FormatScalarValue(item)).Append("\n");
            }
        }

        private static void EncodeExpandedArrayItem(string prefix, int idx, IList arr, StringBuilder outb, int depth, GenericOptions opts)
        {
            if (arr.Count == 0) { outb.Append(prefix).Append("@").Append(idx).Append(" [0]\n"); return; }
            if (AllPrimitives(arr)) { outb.Append(prefix).Append("@").Append(idx).Append(" [").Append(arr.Count).Append("]: ").Append(JoinPrimitives(arr)).Append("\n"); return; }
            var fields = TabularFields(arr);
            if (fields != null) { EncodeTabular(prefix + "@" + idx + " ", arr, fields, outb, depth + 1, opts); return; }
            EncodeExpanded(prefix + "@" + idx + " ", arr, outb, depth + 1, opts);
        }
    }
}
