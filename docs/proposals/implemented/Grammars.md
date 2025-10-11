Below is a **reusable .NET foundation** for grammar‑based parsers/linters that you can plug different languages/schemas into. It’s modeled after Roslyn’s green/red trees, ESLint’s rule model, and SARIF for output. You get:

* **Pluggable parsers** (Pidgin/ANTLR/Tree‑sitter adapters)
* **Incremental syntax trees** with spans & trivia
* **Binding/semantic passes** (optional)
* **Rule engine** with typed visitors & pattern selectors
* **Autofix engine** (text edits) and **SARIF emission**
* **Embeddings** (e.g., Markdown → fenced blocks → inner language)
* **CLI & LSP** surfaces

You can copy this into `Lint.*` projects and add a new language by implementing 2–3 small interfaces.

---

## 1) Core abstractions (language‑agnostic)

```csharp
// Lint.Core
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
    public static TextSpan FromBounds(int start, int end) => new(start, end - start);
}

public interface ISyntaxTrivia { string Text { get; } }
public interface ISyntaxToken
{
    string Kind { get; }
    string Text { get; }
    TextSpan Span { get; }
    IReadOnlyList<ISyntaxTrivia> Leading { get; }
    IReadOnlyList<ISyntaxTrivia> Trailing { get; }
}

public interface ISyntaxNode
{
    string Kind { get; }
    TextSpan Span { get; }                 // full span incl. children
    IEnumerable<ISyntaxNode> Children();   // tokens can be nodes too if you prefer
}

public interface ISyntaxTree
{
    ISyntaxNode Root { get; }
    string SourceText { get; }
    IReadOnlyList<Diagnostic> ParseDiagnostics { get; }
    // Optional incremental reparse hook
    ISyntaxTree WithChanges(params TextChange[] changes);
}

public readonly record struct TextChange(TextSpan Span, string NewText);

// --- Semantic model (optional) ---
public interface ISemanticModel { /* symbols, scopes, types, etc. */ }

// --- Parse/Binder services per language ---
public interface ILanguage
{
    string Name { get; }
    ISyntaxTree Parse(string text, LanguageParseOptions? options = null);
    ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null);
    // Pretty printer or formatter to help safe fixes
    string Print(ISyntaxNode node);
}

public sealed class LanguageParseOptions
{
    public bool Tolerant { get; init; } = true;   // enable error recovery
    public bool CaptureTrivia { get; init; } = true;
}

public sealed class LanguageBindOptions { }

// --- Diagnostics + fixes ---
public enum Severity { Info, Warning, Error }
public sealed record DiagnosticId(string Value)
{
    public static implicit operator string(DiagnosticId id) => id.Value;
    public override string ToString() => Value;
}
public sealed record CodeFix(string Title, IReadOnlyList<TextChange> Edits);

public sealed record Diagnostic(
    DiagnosticId Id,
    Severity Severity,
    string Message,
    TextSpan Span,
    IReadOnlyList<CodeFix> Fixes,
    string? HelpLink = null,
    string? File = null
);
```

---

## 2) Rule engine (typed, composable)

```csharp
// Lint.Core.Rules
public interface IRule
{
    DiagnosticId Id { get; }
    string Title { get; }
    string Description { get; }
    Severity DefaultSeverity { get; }
    IEnumerable<Diagnostic> Analyze(RuleContext ctx);
}

public sealed class RuleContext
{
    public required ILanguage Language { get; init; }
    public required ISyntaxTree Tree { get; init; }
    public ISemanticModel? SemanticModel { get; init; }
    public required string FilePath { get; init; }
    public required CancellationToken Cancel { get; init; }

    // Helpers:
    public IEnumerable<T> DescendantsOfKind<T>(string kind) where T : class, ISyntaxNode =>
        TreeDescendants(Tree.Root).Where(n => n.Kind == kind).Cast<T>();

    private static IEnumerable<ISyntaxNode> TreeDescendants(ISyntaxNode n)
    {
        var stack = new Stack<ISyntaxNode>(); stack.Push(n);
        while (stack.Count > 0)
        {
            var x = stack.Pop(); yield return x;
            foreach (var c in x.Children()) stack.Push(c);
        }
    }
}

// Rule packs
public interface IRuleSet { IReadOnlyList<IRule> Rules { get; } }

public sealed class Linter
{
    private readonly IReadOnlyList<IRule> _rules;
    public Linter(params IRule[] rules) => _rules = rules;

    public IEnumerable<Diagnostic> Run(RuleContext ctx) =>
        _rules.SelectMany(r => r.Analyze(ctx));
}
```

**Selectors (optional but nice):**
Provide a tiny selector DSL for pattern rules:

```csharp
public static class Q // query helpers
{
    public static IEnumerable<ISyntaxNode> OfKind(this ISyntaxNode root, params string[] kinds)
        => L(root).Where(n => kinds.Contains(n.Kind));
    static IEnumerable<ISyntaxNode> L(ISyntaxNode n) { yield return n; foreach (var c in n.Children()) foreach (var d in L(c)) yield return d; }
}
```

---

## 3) Autofix + SARIF

```csharp
// Lint.Sarif
using Microsoft.CodeAnalysis.Sarif;

public static class SarifEmitter
{
    public static SarifLog ToSarif(string artifactUri, string sourceText, IEnumerable<Diagnostic> diagnostics, string toolName = "LintKit", string? version = null)
    {
        var results = new List<Result>();
        var rules = new Dictionary<string, ReportingDescriptor>();

        Region R(TextSpan s, string text)
        {
            // 1-based line/col (compute via line map)
            var lm = new LineMap(text);
            var (sl, sc) = lm.ToLineCol(s.Start);
            var (el, ec) = lm.ToLineCol(s.End);
            return new Region{ StartLine = sl, StartColumn = sc, EndLine = el, EndColumn = ec };
        }

        foreach (var d in diagnostics)
        {
            if (!rules.ContainsKey(d.Id))
                rules[d.Id] = new ReportingDescriptor
                {
                    Id = d.Id,
                    ShortDescription = new(d.Title),
                    FullDescription = new(d.Description),
                };

            var res = new Result
            {
                RuleId = d.Id,
                Message = new(d.Message),
                Level = d.Severity switch {
                    Severity.Info => FailureLevel.Note,
                    Severity.Warning => FailureLevel.Warning,
                    _ => FailureLevel.Error
                },
                Locations = new[]
                {
                    new Location
                    {
                        PhysicalLocation = new()
                        {
                            ArtifactLocation = new() { Uri = new Uri(artifactUri) },
                            Region = R(d.Span, sourceText)
                        }
                    }
                }
            };

            if (d.Fixes.Count > 0)
            {
                var fx = new Fix { Description = new(d.Fixes[0].Title) };
                fx.ArtifactChanges = new List<ArtifactChange>
                {
                    new ArtifactChange
                    {
                        ArtifactLocation = new() { Uri = new Uri(artifactUri) },
                        Replacements = d.Fixes.SelectMany(cf => cf.Edits.Select(e => new Replacement
                        {
                            DeletedRegion = R(e.Span, sourceText),
                            InsertedContent = new ArtifactContent { Text = e.NewText }
                        })).ToList()
                    }
                };
                res.Fixes = new[] { fx };
            }

            results.Add(res);
        }

        return new SarifLog
        {
            Version = SarifVersion.Current,
            Runs = new[]
            {
                new Run
                {
                    Tool = new Tool { Driver = new() { Name = toolName, Version = version, Rules = rules.Values.ToList() } },
                    Artifacts = new[] { new Artifact { Location = new ArtifactLocation { Uri = new Uri(artifactUri) }, Contents = new ArtifactContent { Text = sourceText } } },
                    Results = results
                }
            }
        };
    }

    // simple line map (CRLF & LF)
    private sealed class LineMap
    {
        private readonly int[] _starts;
        public LineMap(string s)
        {
            var list = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') list.Add(i + 1);
            _starts = list.ToArray();
        }
        public (int line, int col) ToLineCol(int idx)
        {
            idx = Math.Clamp(idx, 0, Math.Max(0, _starts[^1]));
            var line = Array.BinarySearch(_starts, idx);
            if (line < 0) line = ~line - 1;
            return (line + 1, idx - _starts[line] + 1);
        }
    }
}
```

---

## 4) Parser backends (pick per language)

### A) Pidgin adapter (PEG/combinators; great for custom DSLs)

```csharp
// Lint.Parsing.Pidgin
public abstract class PidginLanguageBase : ILanguage
{
    public abstract string Name { get; }
    protected abstract Pidgin.Parser<char, ISyntaxNode> Root { get; }

    public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
        => new PidginTree(text, Root);

    public virtual ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null) => null;
    public virtual string Print(ISyntaxNode node) => node switch { _ => node.ToString()! };

    private sealed class PidginTree : ISyntaxTree
    {
        public ISyntaxNode Root { get; }
        public string SourceText { get; }
        public IReadOnlyList<Diagnostic> ParseDiagnostics { get; } = Array.Empty<Diagnostic>();
        public PidginTree(string text, Pidgin.Parser<char, ISyntaxNode> root)
        {
            SourceText = text;
            Root = root.Before(Pidgin.Parser<char>.End()).ParseOrThrow(text);
        }
        public ISyntaxTree WithChanges(params TextChange[] changes)
        {
            // naive (full reparse). You can add incremental later.
            var newText = Apply(SourceText, changes);
            return new PidginTree(newText, ((PidginLanguageBase)default!).Root); // supply root
        }
        static string Apply(string s, IEnumerable<TextChange> edits)
        {
            var ordered = edits.OrderByDescending(e => e.Span.Start);
            var sb = new System.Text.StringBuilder(s);
            foreach (var e in ordered) { sb.Remove(e.Span.Start, e.Span.Length); sb.Insert(e.Span.Start, e.NewText); }
            return sb.ToString();
        }
    }
}
```

### B) ANTLR4 adapter (best when a grammar already exists)

```csharp
// Lint.Parsing.Antlr
public abstract class AntlrLanguageBase<TLexer, TParser, TRoot> : ILanguage
    where TLexer  : Antlr4.Runtime.Lexer
    where TParser : Antlr4.Runtime.Parser
    where TRoot   : Antlr4.Runtime.Tree.IParseTree
{
    public abstract string Name { get; }
    protected abstract TRoot ParseRoot(TParser parser);
    protected abstract ISyntaxNode Convert(TRoot tree, string text, out IReadOnlyList<Diagnostic> parseDiags);

    public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
    {
        var input = new Antlr4.Runtime.AntlrInputStream(text);
        var lex   = (TLexer)Activator.CreateInstance(typeof(TLexer), input)!;
        var tokens= new Antlr4.Runtime.CommonTokenStream(lex);
        var parser= (TParser)Activator.CreateInstance(typeof(TParser), tokens)!;
        parser.ErrorHandler = new Antlr4.Runtime.BailErrorStrategy(); // or tolerant handler you implement
        var root = ParseRoot(parser);
        var node = Convert(root, text, out var diags);
        return new Tree(text, node, diags);
    }

    public virtual ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null) => null;
    public virtual string Print(ISyntaxNode node) => node.ToString()!;

    private sealed class Tree : ISyntaxTree
    {
        public ISyntaxNode Root { get; }
        public string SourceText { get; }
        public IReadOnlyList<Diagnostic> ParseDiagnostics { get; }
        public Tree(string text, ISyntaxNode root, IReadOnlyList<Diagnostic> diags) { SourceText = text; Root = root; ParseDiagnostics = diags; }
        public ISyntaxTree WithChanges(params TextChange[] changes) => throw new NotImplementedException(); // add incremental if needed
    }
}
```

### C) Tree‑sitter adapter (great coverage + incremental)

If you choose Tree‑sitter (via a .NET wrapper), the adapter is analogous: feed bytes → get a concrete syntax tree → map nodes/tokens to `ISyntaxNode`. You get **cheap incremental parses** out of the box.

---

## 5) Embedding host (Markdown, templates, mixed files)

````csharp
// Lint.Embedding
public interface IEmbedding
{
    // Finds embedded regions and returns (language, slice)
    IEnumerable<(ILanguage Language, TextSpan Span)> Find(string sourceText);
}

public sealed class MarkdownMermaidEmbedding : IEmbedding
{
    private static readonly Regex Fence = new(@"(?ms)```(?<lang>[\w\-]+)\s*\n(?<code>.*?)\n```");
    private readonly Func<string, ILanguage?> _langResolver;

    public MarkdownMermaidEmbedding(Func<string, ILanguage?> langResolver) => _langResolver = langResolver;

    public IEnumerable<(ILanguage, TextSpan)> Find(string text)
    {
        foreach (Match m in Fence.Matches(text))
            if (_langResolver(m.Groups["lang"].Value) is { } lang)
                yield return (lang, new TextSpan(m.Groups["code"].Index, m.Groups["code"].Length));
    }
}
````

---

## 6) Example rule (portable across languages)

```csharp
// Example: "no-duplicate-identifiers" for any language that exposes Identifier nodes
public sealed class NoDuplicateIdentifiers<TIdentifierNode> : IRule where TIdentifierNode : ISyntaxNode
{
    public DiagnosticId Id => new("core/no-duplicate-identifiers");
    public string Title => "Identifiers must be unique within scope";
    public string Description => "Detects repeated declarations in the same scope and suggests a new name.";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        var byText = new Dictionary<string, TIdentifierNode>(StringComparer.Ordinal);
        foreach (var id in FindIdentifiers<TIdentifierNode>(ctx.Tree.Root))
        {
            var text = ExtractText(id, ctx.Tree.SourceText);
            if (byText.TryGetValue(text, out var first))
            {
                var suggestion = Suggest(text, byText.Keys);
                yield return new Diagnostic(
                    Id, Severity.Error,
                    $"Duplicate identifier '{text}'.",
                    id.Span,
                    new[] { new CodeFix($"Rename to '{suggestion}'",
                        new[] { new TextChange(id.Span, Replace(ctx.Tree.SourceText, id.Span, text, suggestion)) }) }
                );
            }
            else byText[text] = id;
        }
    }

    static IEnumerable<TIdentifierNode> FindIdentifiers<TIdentifierNode>(ISyntaxNode n)
        => n.Kind switch
        {
            // your adapter exposes consistent kinds; or you use C# pattern matching over node type
            _ => n.Children().OfType<TIdentifierNode>().SelectMany(FindIdentifiers<TIdentifierNode>)
        };

    static string ExtractText(ISyntaxNode node, string text) => text.Substring(node.Span.Start, node.Span.Length);
    static string Replace(string source, TextSpan s, string oldText, string newText) => newText;
    static string Suggest(string basis, IEnumerable<string> taken) { var i=2; var set=new HashSet<string>(taken); while(set.Contains($"{basis}_{i}")) i++; return $"{basis}_{i}"; }
}
```

---

## 7) Putting it together (runner)

```csharp
// Lint.Runner (library or CLI host)
public static class LintRunner
{
    public static SarifLog LintFile(ILanguage lang, string text, string uri, IRuleSet rules, IEmbedding? embedding = null)
    {
        var diags = new List<Diagnostic>();

        if (embedding is null)
        {
            var tree = lang.Parse(text, new LanguageParseOptions { Tolerant = true });
            var ctx  = new RuleContext { Language = lang, Tree = tree, FilePath = uri, Cancel = CancellationToken.None };
            diags.AddRange(new Linter(rules.Rules.ToArray()).Run(ctx));
        }
        else
        {
            foreach (var (childLang, span) in embedding.Find(text))
            {
                var slice = text.Substring(span.Start, span.Length);
                var tree  = childLang.Parse(slice);
                // Re-map rule spans from slice → full text by offsetting
                var ctx   = new RuleContext { Language = childLang, Tree = tree, FilePath = uri, Cancel = CancellationToken.None };
                var linter= new Linter(rules.Rules.ToArray());
                foreach (var d in linter.Run(ctx))
                    diags.Add(d with { Span = TextSpan.FromBounds(span.Start + d.Span.Start, span.Start + d.Span.End) });
            }
        }

        return SarifEmitter.ToSarif(uri, text, diags, toolName: "LintKit");
    }
}
```

---

## 8) How to add a new language (update‑by‑example)

1. **Pick a backend**:

    * *Custom DSLs*: derive from `PidginLanguageBase`, write a small PEG grammar (start with tokens, then statements).
    * *Existing grammars*: derive from `AntlrLanguageBase<...>`, drop in grammar + generated lexer/parser, map to `ISyntaxNode`.
    * *Broad coverage*: a Tree‑sitter adapter for languages with mature grammars.

2. **Expose stable node kinds** (“Identifier”, “FunctionDecl”, “Edge”, “Participant”…). Keep a **compat layer** so rules can be reused across languages that share concepts.

3. **Write 2–3 generic rules** using those kinds. Add language‑specific rules in small packs.

4. **Tests**: snapshot AST shape + diagnostics; add a failing example and fix the grammar/rule locally (“update‑by‑example”).

---

## 9) Why this foundation holds up

* **Separation of concerns**: parsing → (optional) binding → rules → fixes → SARIF.
* **Incrementality**: easy to add (via red/green or Tree‑sitter).
* **Cross‑language reuse**: rules can target abstract kinds or typed nodes.
* **Safe edits**: spans + trivia + printer enable non‑destructive fixes.
* **Embeddings**: handle Markdown, templating, or polyglot files uniformly.
* **Surfaces**: same engine powers CLI, CI, and LSP (squiggles).

---

If you want, I can drop in a concrete **Pidgin language sample** (e.g., `flowchart` or a JSON‑like DSL) wired to two rules and a CLI so you can clone and extend.
