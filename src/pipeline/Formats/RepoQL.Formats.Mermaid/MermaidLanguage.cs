using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Diagnostics;
using RepoQL.Formats.Mermaid.Language;
using RepoQL.Formats.Mermaid.Syntax;

namespace RepoQL.Formats.Mermaid;

using System.Text.RegularExpressions;

/// <summary>
/// Tolerant Mermaid parser (flowchart/graph, sequenceDiagram, pie) using regex + line scanning.
/// Captures spans for ids, labels, message texts, and numeric values.
/// </summary>
public sealed class MermaidLanguage : ILanguage
{
    public string Name => "Mermaid";

    public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
    {
        var (doc, diags) = ParseDocument(text);
        return new Tree(text, doc, diags);
    }

    public ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null) => null;

    public string Print(ISyntaxNode node) => node switch
    {
        FlowNodeDecl { LabelQuoted: true } n => $"{n.Id}{n.ShapeOpen}\"{n.Label}\"{n.ShapeClose}",
        FlowNodeDecl n => $"{n.Id}{n.ShapeOpen}{n.Label}{n.ShapeClose}",
        FlowEdge { MidLabel: { } ml } e => $"{e.Src} {e.Arrow} |{ml}| {e.Dst}",
        FlowEdge e => $"{e.Src} {e.Arrow} {e.Dst}",
        SeqParticipant { Alias: { } a } p => $"{p.Keyword} {p.Name} as {a}",
        SeqParticipant p => $"{p.Keyword} {p.Name}",
        SeqMessage m => $"{m.From}{m.Arrow}{m.To}: {m.Text}",
        PieEntry { LabelQuoted: true } pe => $"\"{pe.LabelRaw}\" : {pe.Value}",
        PieEntry pe => $"{pe.LabelRaw} : {pe.Value}",
        _ => node.ToString() ?? string.Empty
    };

    private static (MDocument Root, IReadOnlyList<Diagnostic> Diags) ParseDocument(string text)
    {
        var diags = new List<Diagnostic>();
        var items = new List<MStmt>();

        static bool IsEol(char c) => c == '\n' || c == '\r';
        var Len = () => text.Length;
        int ReadLineEnd(int start) { var j = start; while (j < Len() && !IsEol(text[j])) j++; return j; }
        void SkipEol(ref int p) { if (p < Len() && text[p] == '\r') p++; if (p < Len() && text[p] == '\n') p++; }

        // header
        var pos = 0; var le = ReadLineEnd(pos); var header = text[pos..le].Trim();
        var kind = ParseHeader(header) ?? "unknown";
        while (kind == "unknown" && pos < Len()) { pos = le; SkipEol(ref pos); le = ReadLineEnd(pos); header = text[pos..le].Trim(); kind = ParseHeader(header) ?? "unknown"; }
        pos = le; SkipEol(ref pos);

        while (pos < Len())
        {
            var ls = pos; le = ReadLineEnd(ls); var line = text[ls..le];
            if (!string.IsNullOrWhiteSpace(line))
            {
                items.Add(kind switch
                {
                    "flowchart" => ParseFlowLine(line, ls, le),
                    "sequenceDiagram" => ParseSeqLine(line, ls, le),
                    "pie" => ParsePieLine(line, ls, le),
                    _ => new UnknownStmt(line, TextSpan.FromBounds(ls, le))
                });
            }
            pos = le; SkipEol(ref pos);
        }

        return (new MDocument(kind, items, TextSpan.FromBounds(0, text.Length)), diags);
    }

    private static string? ParseHeader(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.TrimStart();
        if (t.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase)) return "flowchart";
        if (t.StartsWith("graph", StringComparison.OrdinalIgnoreCase)) return "flowchart";
        if (t.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)) return "sequenceDiagram";
        if (t.StartsWith("pie", StringComparison.OrdinalIgnoreCase)) return "pie";
        return null;
    }

    // Flowchart
    private static readonly Regex RxFlowNode = new(
        pattern: @"^(?<id>[A-Za-z_][A-Za-z0-9_\-]*)\s*(?<open>[\[\(\{])(?<label>.*?)(?<close>[\]\)\}])?$",
        options: RegexOptions.Compiled);

    private static readonly Regex RxFlowEdge = new(
        pattern: @"^(?<src>[A-Za-z_][A-Za-z0-9_\-]*)\s+(?<arrow>(?:-->|-\.->|==>|---|--x|--o|->|->>))\s+(?:\|(?<mid>[^|]*)\|\s+)?(?<dst>[A-Za-z_][A-Za-z0-9_\-]*)$",
        options: RegexOptions.Compiled);

    private static MStmt ParseFlowLine(string line, int ls, int le)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed.Substring("subgraph ".Length).Trim();
            return new FlowSubgraphStart(name, TextSpan.FromBounds(ls, le));
        }
        if (string.Equals(trimmed, "end", StringComparison.OrdinalIgnoreCase))
        {
            return new FlowEnd(TextSpan.FromBounds(ls, le));
        }
        if (trimmed.StartsWith("classDef ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed.Substring("classDef ".Length);
            var idx = rest.IndexOf(' ');
            var name = idx > 0 ? rest[..idx].Trim() : rest.Trim();
            var attrs = idx > 0 ? rest[(idx + 1)..].Trim() : string.Empty;
            return new ClassDef(name, attrs, TextSpan.FromBounds(ls, le));
        }
        if (trimmed.StartsWith("click ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed.Substring("click ".Length).Trim();
            var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var nodeId = parts.Length > 0 ? parts[0] : string.Empty;
            string? href = null; string? tooltip = null; string? link = null;
            if (parts.Length == 2)
            {
                var arg = parts[1];
                var hrefMatch = Regex.Match(arg, "href\\s+\"([^\"]+)\"");
                if (hrefMatch.Success) href = hrefMatch.Groups[1].Value;
                var quotes = Regex.Matches(arg, "\"([^\"]*)\"");
                if (quotes.Count > 0) tooltip = quotes[0].Groups[1].Value;
                if (quotes.Count > 1) link = quotes[1].Groups[1].Value;
            }
            return new ClickStmt(nodeId, href, tooltip, link, TextSpan.FromBounds(ls, le));
        }
        var mn = RxFlowNode.Match(line);
        if (mn.Success)
        {
            var id = mn.Groups["id"].Value;
            var open = mn.Groups["open"].Value[0];
            var close = open switch { '[' => ']', '(' => ')', '{' => '}', _ => ']' };
            var labelRaw = mn.Groups["label"].Value ?? string.Empty;
            var quoted = labelRaw.StartsWith('"') && labelRaw.EndsWith('"');
            var label = quoted && labelRaw.Length >= 2 ? labelRaw[1..^1] : labelRaw;
            var closed = mn.Groups["close"].Success;
            var idSpan = new TextSpan(ls + mn.Groups["id"].Index, mn.Groups["id"].Length);
            var labelStart = ls + mn.Groups["open"].Index + 1;
            var labelEnd = closed ? ls + mn.Groups["close"].Index : le;
            var labelSpan = new TextSpan(labelStart, Math.Max(0, labelEnd - labelStart));
            return new FlowNodeDecl(id, open, close, label, quoted, closed, idSpan, labelSpan, TextSpan.FromBounds(ls, le));
        }

        var me = RxFlowEdge.Match(line);
        if (me.Success)
        {
            var src = me.Groups["src"].Value;
            var arrow = me.Groups["arrow"].Value;
            var mid = me.Groups["mid"].Success ? me.Groups["mid"].Value : null;
            var dst = me.Groups["dst"].Value;
            var srcSpan = new TextSpan(ls + me.Groups["src"].Index, me.Groups["src"].Length);
            var arrowSpan = new TextSpan(ls + me.Groups["arrow"].Index, me.Groups["arrow"].Length);
            var dstSpan = new TextSpan(ls + me.Groups["dst"].Index, me.Groups["dst"].Length);
            TextSpan? midSpan = me.Groups["mid"].Success ? new TextSpan(ls + me.Groups["mid"].Index, me.Groups["mid"].Length) : null;
            return new FlowEdge(src, arrow, mid, dst, srcSpan, arrowSpan, midSpan, dstSpan, TextSpan.FromBounds(ls, le));
        }

        return new UnknownStmt(line, TextSpan.FromBounds(ls, le));
    }

    // Sequence
    private static readonly Regex RxSeqPart = new(
        pattern: @"^(?<kw>participant|actor)\s+(?<name>[A-Za-z_][A-Za-z0-9_\-]*)(?:\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_\-]*))?$",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RxSeqMsg = new(
        pattern: @"^(?<from>[A-Za-z_][A-Za-z0-9_\-]*)(?<arr>-{1,2}(?:>>|x|\)|>)?)\s*(?<to>[A-Za-z_][A-Za-z0-9_\-]*)\s*:\s*(?<text>.*)$",
        options: RegexOptions.Compiled);

    private static MStmt ParseSeqLine(string line, int ls, int le)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("alt ", StringComparison.OrdinalIgnoreCase))
            return new SeqBlockStart("alt", trimmed[4..], TextSpan.FromBounds(ls, le));
        if (trimmed.StartsWith("opt ", StringComparison.OrdinalIgnoreCase))
            return new SeqBlockStart("opt", trimmed[4..], TextSpan.FromBounds(ls, le));
        if (trimmed.StartsWith("loop ", StringComparison.OrdinalIgnoreCase))
            return new SeqBlockStart("loop", trimmed[5..], TextSpan.FromBounds(ls, le));
        if (string.Equals(trimmed, "end", StringComparison.OrdinalIgnoreCase))
            return new SeqEnd(TextSpan.FromBounds(ls, le));
        var mp = RxSeqPart.Match(line);
        if (mp.Success)
        {
            var kw = mp.Groups["kw"].Value;
            var name = mp.Groups["name"].Value;
            var alias = mp.Groups["alias"].Success ? mp.Groups["alias"].Value : null;
            var nameSpan = new TextSpan(ls + mp.Groups["name"].Index, mp.Groups["name"].Length);
            return new SeqParticipant(kw, name, alias, nameSpan, TextSpan.FromBounds(ls, le));
        }

        var mm = RxSeqMsg.Match(line);
        if (mm.Success)
        {
            var from = mm.Groups["from"].Value;
            var arr = mm.Groups["arr"].Value;
            var to = mm.Groups["to"].Value;
            var textV = mm.Groups["text"].Value;
            var textSpan = new TextSpan(ls + mm.Groups["text"].Index, mm.Groups["text"].Length);
            return new SeqMessage(from, arr, to, textV, textSpan, TextSpan.FromBounds(ls, le));
        }

        return new UnknownStmt(line, TextSpan.FromBounds(ls, le));
    }

    // Pie
    private static readonly Regex RxPie = new(
        pattern: @"^(?<label>""[^""]*""|[^:""]+)\s*:\s*(?<num>[-+]?\d+(?:\.\d+)?)\s*$",
        options: RegexOptions.Compiled);

    private static MStmt ParsePieLine(string line, int ls, int le)
    {
        var m = RxPie.Match(line);
        if (m.Success)
        {
            var lblRaw = m.Groups["label"].Value.Trim();
            var quoted = lblRaw.Length >= 2 && lblRaw[0] == '"' && lblRaw[^1] == '"';
            if (quoted) lblRaw = lblRaw[1..^1];
            var numText = m.Groups["num"].Value;
            var val = double.TryParse(numText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            var lblSpan = new TextSpan(ls + m.Groups["label"].Index, m.Groups["label"].Length);
            var numSpan = new TextSpan(ls + m.Groups["num"].Index, m.Groups["num"].Length);
            return new PieEntry(lblRaw, quoted, val, lblSpan, numSpan, TextSpan.FromBounds(ls, le));
        }
        return new UnknownStmt(line, TextSpan.FromBounds(ls, le));
    }

    private sealed class Tree(string text, ISyntaxNode root, IReadOnlyList<Diagnostic> diags)
        : ISyntaxTree
    {
        public ISyntaxNode Root { get; } = root;
        public string SourceText { get; } = text;
        public IReadOnlyList<Diagnostic> ParseDiagnostics { get; } = diags;

        public ISyntaxTree WithChanges(params TextChange[] changes)
        {
            if (changes is null || changes.Length == 0) return this;
            var ordered = changes.OrderByDescending(c => c.Span.Start);
            var sb = new System.Text.StringBuilder(SourceText);
            foreach (var c in ordered) { sb.Remove(c.Span.Start, c.Span.Length); sb.Insert(c.Span.Start, c.NewText); }
            var lang = new MermaidLanguage();
            return lang.Parse(sb.ToString(), new LanguageParseOptions { Tolerant = true });
        }
    }
}

