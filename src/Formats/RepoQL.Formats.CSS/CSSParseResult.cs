namespace RepoQL.Formats.CSS;

public sealed class CSSParseResult
{
    // Common CSS constructs
    public List<CSSRulesetInfo> Rulesets { get; } = [];
    public List<CSSImportInfo> Imports { get; } = [];
    public List<CSSMediaInfo> MediaRules { get; } = [];
    public List<CSSKeyframesInfo> Keyframes { get; } = [];
    public List<CSSFontFaceInfo> FontFaces { get; } = [];
    public List<CSSSupportsInfo> SupportsRules { get; } = [];
    public List<CSSCharsetInfo> Charsets { get; } = [];
    public List<CSSNamespaceInfo> Namespaces { get; } = [];
    public List<CSSPageInfo> Pages { get; } = [];

    // SCSS-specific constructs
    public List<SCSSVariableInfo> Variables { get; } = [];
    public List<SCSSMixinInfo> Mixins { get; } = [];
    public List<SCSSIncludeInfo> Includes { get; } = [];
    public List<SCSSExtendInfo> Extends { get; } = [];
    public List<SCSSFunctionInfo> Functions { get; } = [];
}

// CSS constructs
public sealed class CSSRulesetInfo
{
    public required string Selector { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSImportInfo
{
    public required string Path { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSMediaInfo
{
    public required string Condition { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSKeyframesInfo
{
    public required string Name { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSFontFaceInfo
{
    public required CSSSpan Span { get; init; }
}

public sealed class CSSSupportsInfo
{
    public required string Condition { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSCharsetInfo
{
    public required string Charset { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSNamespaceInfo
{
    public string? Prefix { get; init; }
    public required string Uri { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class CSSPageInfo
{
    public string? PseudoPage { get; init; }
    public required CSSSpan Span { get; init; }
}

// SCSS-specific constructs
public sealed class SCSSVariableInfo
{
    public required string Name { get; init; }
    public string? Value { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class SCSSMixinInfo
{
    public required string Name { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class SCSSIncludeInfo
{
    public required string Name { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class SCSSExtendInfo
{
    public required string Extended { get; init; }
    public required CSSSpan Span { get; init; }
}

public sealed class SCSSFunctionInfo
{
    public required string Name { get; init; }
    public required CSSSpan Span { get; init; }
}

public readonly record struct CSSSpan(int Start, int End);
