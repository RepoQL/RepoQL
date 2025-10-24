namespace RepoQL.Web.Services;

public sealed record SnippetLine(
    int LineNumber,
    string Text,
    bool IsFocus,
    int? FocusStartColumn,
    int? FocusEndColumn,
    string Language);