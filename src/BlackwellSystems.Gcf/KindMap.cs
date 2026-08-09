using System.Collections.Generic;

namespace BlackwellSystems.Gcf
{
    internal static class KindMap
    {
        internal static readonly Dictionary<string, string> Abbrev = new Dictionary<string, string>
        {
            { "function", "fn" }, { "type", "type" }, { "method", "method" }, { "interface", "iface" },
            { "var", "var" }, { "const", "const" }, { "resource", "resource" }, { "table", "table" },
            { "class", "class" }, { "selector", "selector" }, { "field", "field" }, { "route_handler", "route" },
            { "external", "ext" }, { "file", "file" }, { "package", "pkg" }, { "service", "svc" }
        };

        internal static readonly Dictionary<string, string> Expand = new Dictionary<string, string>
        {
            { "fn", "function" }, { "type", "type" }, { "method", "method" }, { "iface", "interface" },
            { "var", "var" }, { "const", "const" }, { "resource", "resource" }, { "table", "table" },
            { "class", "class" }, { "selector", "selector" }, { "field", "field" }, { "route", "route_handler" },
            { "ext", "external" }, { "file", "file" }, { "pkg", "package" }, { "svc", "service" }
        };

        internal static string AbbreviateKind(string kind) => Abbrev.TryGetValue(kind, out var v) ? v : kind;
        internal static string ExpandKind(string kind) => Expand.TryGetValue(kind, out var v) ? v : kind;
    }
}
