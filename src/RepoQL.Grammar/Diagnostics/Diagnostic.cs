using RepoQL.Grammar.Core;

namespace RepoQL.Grammar.Diagnostics;

public sealed record Diagnostic(
    DiagnosticId Id,
    Severity Severity,
    string Message,
    TextSpan Span,
    IReadOnlyList<CodeFix> Fixes,
    string? HelpLink = null,
    string? File = null
);