using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;

namespace BlackwellSystems.Gcf.Cli
{
    internal static class Program
    {
        private const string Usage = @"gcf - token-optimized wire format for LLM tool responses

Usage:
  gcf encode [file]           Encode a JSON graph payload to GCF (stdin if no file)
  gcf decode [file]           Decode GCF graph text to JSON (stdin if no file)
  gcf encode-generic [file]   Encode generic JSON to GCF (stdin if no file)
  gcf decode-generic [file]   Decode generic GCF to JSON (stdin if no file)
  gcf stats [file]            Compare token counts: JSON vs GCF (stdin if no file)

Examples:
  gcf encode < payload.json
  gcf decode < payload.gcf
  gcf encode-generic < data.json
  gcf decode-generic < data.gcf
  gcf stats payload.json
";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.Write(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                string cmd = args[0];
                string input = ReadInput(args.Length > 1 ? args[1] : null);

                switch (cmd)
                {
                    case "encode": Console.Out.Write(Gcf.Encode(PayloadFromJson(input))); break;
                    case "decode": Console.Out.Write(PayloadToJson(Gcf.Decode(input))); break;
                    case "encode-generic": Console.Out.Write(Gcf.EncodeGeneric(FromJson(input))); break;
                    case "decode-generic": Console.Out.Write(NativeToJson(Gcf.DecodeGeneric(input))); break;
                    case "stats": DoStats(input); break;
                    default:
                        Console.Error.WriteLine($"gcf: unknown command '{cmd}'\n");
                        Console.Error.Write(Usage);
                        return 1;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("gcf: " + ex.Message);
                return 1;
            }
        }

        private static string ReadInput(string? file)
            => file != null ? File.ReadAllText(file) : Console.In.ReadToEnd();

        // ---- stats (graph payload token comparison) ----
        private static void DoStats(string data)
        {
            var p = PayloadFromJson(data);
            var gcf = Gcf.Encode(p);

            int jsonTokens = data.Trim().Length / 4;
            int gcfTokens = gcf.Trim().Length / 4;
            double savings = jsonTokens > 0 ? 100.0 * (1 - (double)gcfTokens / jsonTokens) : 0;

            const int barWidth = 30;
            string jsonBar = new string('█', barWidth);
            int gcfFilled = jsonTokens > 0 ? (int)Math.Round((double)gcfTokens * barWidth / jsonTokens) : 0;
            gcfFilled = Math.Clamp(gcfFilled, 0, barWidth);
            string gcfBar = new string('█', gcfFilled) + new string('░', barWidth - gcfFilled);

            Console.WriteLine($"Payload: {p.Symbols.Count} symbols, {p.Edges.Count} edges\n");
            Console.WriteLine($"  JSON  {jsonBar}  {jsonTokens} tokens");
            Console.WriteLine($"  GCF   {gcfBar}  {gcfTokens} tokens");
            Console.WriteLine($"\n  Savings: {Math.Round(savings)}% fewer tokens with GCF");
        }

        // ---- JSON <-> native generic model ----
        private static object? FromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return FromJson(doc.RootElement);
        }

        private static object? FromJson(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var m = new OrderedMap();
                    foreach (var p in e.EnumerateObject()) m[p.Name] = FromJson(p.Value);
                    return m;
                case JsonValueKind.Array:
                    var l = new List<object?>();
                    foreach (var it in e.EnumerateArray()) l.Add(FromJson(it));
                    return l;
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number:
                    var raw = e.GetRawText();
                    if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0 && e.TryGetInt64(out var lv)) return lv;
                    return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        private static string NativeToJson(object? v)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                WriteNative(w, v);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteNative(Utf8JsonWriter w, object? v)
        {
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case long l: w.WriteNumberValue(l); break;
                case int i: w.WriteNumberValue(i); break;
                case double d: w.WriteNumberValue(d); break;
                case string s: w.WriteStringValue(s); break;
                case OrderedMap m:
                    w.WriteStartObject();
                    foreach (var kv in m) { w.WritePropertyName(kv.Key); WriteNative(w, kv.Value); }
                    w.WriteEndObject();
                    break;
                case IList list:
                    w.WriteStartArray();
                    foreach (var item in list) WriteNative(w, item);
                    w.WriteEndArray();
                    break;
                default: w.WriteStringValue(v.ToString()); break;
            }
        }

        // ---- graph Payload <-> JSON ----
        private static Payload PayloadFromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(JsonElement e, string k, string def = "") => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;
            int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

            var payload = new Payload
            {
                Tool = S(r, "tool"),
                TokenBudget = I(r, "tokenBudget"),
                TokensUsed = I(r, "tokensUsed"),
                PackRoot = S(r, "packRoot"),
            };
            if (r.TryGetProperty("symbols", out var syms) && syms.ValueKind == JsonValueKind.Array)
                foreach (var s in syms.EnumerateArray())
                    payload.Symbols.Add(new Symbol
                    {
                        QualifiedName = S(s, "qualifiedName"),
                        Kind = S(s, "kind"),
                        Score = s.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0,
                        Provenance = S(s, "provenance"),
                        Distance = I(s, "distance"),
                    });
            if (r.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                foreach (var ed in edges.EnumerateArray())
                    payload.Edges.Add(new Edge
                    {
                        Source = S(ed, "source"),
                        Target = S(ed, "target"),
                        EdgeType = S(ed, "edgeType"),
                        Status = S(ed, "status"),
                    });
            return payload;
        }

        private static string PayloadToJson(Payload p)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("tool", p.Tool);
                w.WriteNumber("tokenBudget", p.TokenBudget);
                w.WriteNumber("tokensUsed", p.TokensUsed);
                w.WriteString("packRoot", p.PackRoot);
                w.WriteStartArray("symbols");
                foreach (var s in p.Symbols)
                {
                    w.WriteStartObject();
                    w.WriteString("qualifiedName", s.QualifiedName);
                    w.WriteString("kind", s.Kind);
                    w.WriteNumber("score", s.Score);
                    w.WriteString("provenance", s.Provenance);
                    w.WriteNumber("distance", s.Distance);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("edges");
                foreach (var e in p.Edges)
                {
                    w.WriteStartObject();
                    w.WriteString("source", e.Source);
                    w.WriteString("target", e.Target);
                    w.WriteString("edgeType", e.EdgeType);
                    w.WriteString("status", e.Status);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
