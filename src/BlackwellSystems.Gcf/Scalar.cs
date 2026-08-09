using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackwellSystems.Gcf
{
    /// <summary>Scalar value formatting and parsing per SPEC Section 2.</summary>
    internal static class Scalar
    {
        private static readonly Regex JsonNumberRe =
            new Regex(@"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$", RegexOptions.Compiled);
        private static readonly Regex NumericLikeRe =
            new Regex(@"^[+-]\.?\d|^\.\d|^0\d", RegexOptions.Compiled);
        private static readonly Regex BareKeyRe =
            new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex InlineArrayRe =
            new Regex(@"\[[^\]]*\]\s*:", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedScalars =
            new HashSet<string> { "-", "~", "^", "true", "false" };

        public static bool NeedsQuote(string s)
        {
            if (s.Length == 0) return true;
            if (ReservedScalars.Contains(s)) return true;
            // A value shaped like an inline-schema attachment marker (^{...}) would
            // decode as an attachment and lose the string, so it must be quoted.
            if (s.Length >= 3 && s.StartsWith("^{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal)) return true;
            if (JsonNumberRe.IsMatch(s)) return true;
            if (NumericLikeRe.IsMatch(s)) return true;
            if (s[0] == ' ' || s[s.Length - 1] == ' ') return true;
            if (s[0] == '#' || s[0] == '@' || s[0] == '.') return true;
            if (InlineArrayRe.IsMatch(s)) return true;
            foreach (char c in s)
            {
                int code = c;
                if (c == '"' || c == '\\' || c == '|' || c == ',' || code < 0x20 || c == '\n' || c == '\r') return true;
                if (code >= 0x80 && code <= 0x9F) return true; // C1 controls
                if (code > 0x7F && (code == 0xA0 || code == 0x1680 || code == 0x2028 || code == 0x2029 ||
                                    code == 0x202F || code == 0x205F || code == 0x3000 || code == 0xFEFF)) return true;
                if (code >= 0x2000 && code <= 0x200A) return true; // Unicode spaces
            }
            return false;
        }

        public static string QuoteString(string s)
        {
            var outb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': outb.Append("\\\""); break;
                    case '\\': outb.Append("\\\\"); break;
                    case '\b': outb.Append("\\b"); break;
                    case '\f': outb.Append("\\f"); break;
                    case '\n': outb.Append("\\n"); break;
                    case '\r': outb.Append("\\r"); break;
                    case '\t': outb.Append("\\t"); break;
                    default:
                        if (c < 0x20) outb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else outb.Append(c);
                        break;
                }
            }
            outb.Append('"');
            return outb.ToString();
        }

        public static string FormatScalarValue(object? v, char delimiter = '\0')
        {
            if (v == null) return "-";
            switch (v)
            {
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return FormatNumberValue(d);
                case float f: return FormatNumberValue(f);
                case string s:
                    return (NeedsQuote(s) || (delimiter != '\0' && s.IndexOf(delimiter) >= 0)) ? QuoteString(s) : s;
                default:
                    var str = v.ToString() ?? "";
                    return (NeedsQuote(str) || (delimiter != '\0' && str.IndexOf(delimiter) >= 0)) ? QuoteString(str) : str;
            }
        }

        public static string FormatNumberValue(double f)
        {
            if (double.IsNaN(f) || double.IsInfinity(f)) return "0";
            // Negative zero canonicalizes to 0 (SPEC 2.3.1).
            if (f == 0.0) return "0";
            double a = Math.Abs(f);
            string sign = f < 0 ? "-" : "";
            var (sig, sciExp) = Decompose(a);
            if (a >= 1e-6 && a < 1e21)
            {
                return sign + ToPlain(sig, sciExp);
            }
            // Exponent notation: lowercase e, explicit sign, no leading exponent zeros.
            string mantissa = sig.Length > 1 ? sig[0] + "." + sig.Substring(1) : sig;
            string es = sciExp < 0 ? "-" : "+";
            return sign + mantissa + "e" + es + Math.Abs(sciExp).ToString(CultureInfo.InvariantCulture);
        }

        // Decompose a positive double into its shortest significant digits and the
        // decimal exponent of the leading digit (scientific exponent). Relies on the
        // runtime's shortest round-trippable "R" formatting (exact on .NET Core 3+).
        private static (string sig, int sciExp) Decompose(double a)
        {
            string r = a.ToString("R", CultureInfo.InvariantCulture).Replace("e", "E");
            int exp = 0;
            string mant = r;
            int ePos = r.IndexOf('E');
            if (ePos >= 0)
            {
                exp = int.Parse(r.Substring(ePos + 1), CultureInfo.InvariantCulture);
                mant = r.Substring(0, ePos);
            }
            int dot = mant.IndexOf('.');
            string intPart, fracPart;
            if (dot >= 0) { intPart = mant.Substring(0, dot); fracPart = mant.Substring(dot + 1); }
            else { intPart = mant; fracPart = ""; }
            string allDigits = intPart + fracPart;
            int firstNonZero = 0;
            while (firstNonZero < allDigits.Length && allDigits[firstNonZero] == '0') firstNonZero++;
            if (firstNonZero == allDigits.Length) return ("0", 0);
            int sciExp = (intPart.Length - 1 - firstNonZero) + exp;
            string sig = allDigits.Substring(firstNonZero).TrimEnd('0');
            if (sig.Length == 0) sig = "0";
            return (sig, sciExp);
        }

        private static string ToPlain(string sig, int sciExp)
        {
            if (sciExp >= 0)
            {
                if (sig.Length <= sciExp + 1)
                {
                    return sig + new string('0', sciExp + 1 - sig.Length);
                }
                return sig.Substring(0, sciExp + 1) + "." + sig.Substring(sciExp + 1);
            }
            return "0." + new string('0', -sciExp - 1) + sig;
        }

        public static string FormatKeyValue(string s) => BareKeyRe.IsMatch(s) ? s : QuoteString(s);

        // --- Parsing ---

        public enum ScalarKind { Null, Bool, Int, Double, String, Missing, Attachment, InlineAttachment }

        public readonly struct ScalarParsed
        {
            public readonly ScalarKind Kind;
            public readonly object? Value;   // bool, long, double, or string
            public readonly string? Schema;  // for InlineAttachment

            public ScalarParsed(ScalarKind kind, object? value = null, string? schema = null)
            {
                Kind = kind; Value = value; Schema = schema;
            }
        }

        public static ScalarParsed ParseScalarValue(string s, bool tabularContext = false)
        {
            if (s.Length == 0) return new ScalarParsed(ScalarKind.String, "");
            if (s[0] == '"') return new ScalarParsed(ScalarKind.String, ParseQuotedStringValue(s));
            if (s == "-") return new ScalarParsed(ScalarKind.Null);
            if (s == "~")
            {
                if (!tabularContext) throw new ArgumentException("invalid_missing: ~ outside tabular row cell");
                return new ScalarParsed(ScalarKind.Missing);
            }
            if (s == "^" || (s.StartsWith("^{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal)))
            {
                if (!tabularContext) throw new ArgumentException("invalid_attachment_marker: ^ outside tabular row cell");
                if (s == "^") return new ScalarParsed(ScalarKind.Attachment);
                return new ScalarParsed(ScalarKind.InlineAttachment, null, s.Substring(1));
            }
            if (s == "true") return new ScalarParsed(ScalarKind.Bool, true);
            if (s == "false") return new ScalarParsed(ScalarKind.Bool, false);
            if (JsonNumberRe.IsMatch(s))
            {
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0 && Math.Abs(d) <= (double)(1L << 53))
                    {
                        return new ScalarParsed(ScalarKind.Int, (long)d);
                    }
                    return new ScalarParsed(ScalarKind.Double, d);
                }
            }
            return new ScalarParsed(ScalarKind.String, s);
        }

        public static string ParseQuotedStringValue(string s)
        {
            if (s.Length < 2 || s[0] != '"') throw new ArgumentException("unterminated_quote");
            var outb = new StringBuilder();
            int i = 1;
            while (i < s.Length)
            {
                if (s[i] == '"')
                {
                    if (i + 1 != s.Length) throw new ArgumentException("trailing_characters: after closing quote");
                    return outb.ToString();
                }
                if (s[i] == '\\')
                {
                    if (i + 1 >= s.Length) throw new ArgumentException("unterminated_quote");
                    i++;
                    switch (s[i])
                    {
                        case '"': outb.Append('"'); break;
                        case '\\': outb.Append('\\'); break;
                        case '/': outb.Append('/'); break;
                        case 'b': outb.Append('\b'); break;
                        case 'f': outb.Append('\f'); break;
                        case 'n': outb.Append('\n'); break;
                        case 'r': outb.Append('\r'); break;
                        case 't': outb.Append('\t'); break;
                        case 'u':
                            {
                                if (i + 4 >= s.Length) throw new ArgumentException("invalid_escape: incomplete unicode");
                                string hex = s.Substring(i + 1, 4);
                                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                    throw new ArgumentException("invalid_escape: invalid unicode \\u" + hex);
                                if (code >= 0xD800 && code <= 0xDBFF)
                                {
                                    if (i + 10 >= s.Length || s[i + 5] != '\\' || s[i + 6] != 'u')
                                        throw new ArgumentException("invalid_surrogate: isolated high surrogate");
                                    string hex2 = s.Substring(i + 7, 4);
                                    if (!int.TryParse(hex2, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int low))
                                        throw new ArgumentException("invalid_surrogate: invalid low surrogate");
                                    if (low < 0xDC00 || low > 0xDFFF) throw new ArgumentException("invalid_surrogate: expected low surrogate");
                                    int combined = 0x10000 + (code - 0xD800) * 0x400 + (low - 0xDC00);
                                    outb.Append(char.ConvertFromUtf32(combined));
                                    i += 11; continue;
                                }
                                if (code >= 0xDC00 && code <= 0xDFFF) throw new ArgumentException("invalid_surrogate: isolated low surrogate");
                                outb.Append((char)code);
                                i += 5; continue;
                            }
                        default: throw new ArgumentException("invalid_escape: unknown \\" + s[i]);
                    }
                    i++; continue;
                }
                if (s[i] < 0x20) throw new ArgumentException("invalid_escape: unescaped control U+" + ((int)s[i]).ToString("x4", CultureInfo.InvariantCulture));
                outb.Append(s[i]);
                i++;
            }
            throw new ArgumentException("unterminated_quote");
        }

        public static List<string> SplitRespectingQuotes(string s, char delim)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuote = false, escaped = false;
            foreach (char c in s)
            {
                if (escaped) { current.Append(c); escaped = false; continue; }
                if (c == '\\' && inQuote) { current.Append(c); escaped = true; continue; }
                if (c == '"') { inQuote = !inQuote; current.Append(c); continue; }
                if (c == delim && !inQuote) { parts.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            parts.Add(current.ToString());
            return parts;
        }

        public static List<string> SplitFieldDeclValue(string s)
        {
            if (s.Length < 2 || s[0] != '{') throw new ArgumentException("invalid field declaration: " + s);
            int? close = FindClosingBraceIdx(s);
            if (close == null) throw new ArgumentException("invalid field declaration: " + s);
            string inner = s.Substring(1, close.Value - 1);
            if (inner.Length == 0) return new List<string>();
            var raw = SplitRespectingQuotes(inner, ',');
            var fields = new List<string>();
            var seen = new HashSet<string>();
            foreach (var f in raw)
            {
                string trimmed = f.Trim();
                string name;
                if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                {
                    name = ParseQuotedStringValue(trimmed);
                }
                else
                {
                    if (!BareKeyRe.IsMatch(trimmed)) throw new ArgumentException("invalid field name: " + trimmed);
                    name = trimmed;
                }
                if (seen.Contains(name)) throw new ArgumentException("duplicate_field_name: " + name);
                seen.Add(name);
                fields.Add(name);
            }
            return fields;
        }

        public static int? FindClosingBraceIdx(string s)
        {
            bool inQuote = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inQuote) { escaped = true; continue; }
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == '}' && !inQuote) return i;
            }
            return null;
        }
    }
}
