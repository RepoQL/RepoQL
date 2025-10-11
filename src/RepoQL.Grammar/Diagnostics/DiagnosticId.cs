namespace RepoQL.Grammar.Diagnostics;

public sealed record DiagnosticId(string Value)
{
    public static implicit operator string(DiagnosticId id) => id.Value;
    public override string ToString() => Value;
}