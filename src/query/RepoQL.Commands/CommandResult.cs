namespace RepoQL.Commands;

/// <summary>
/// Purpose: Transport-agnostic result from a command handler.
/// Complexity: Thin record with factory methods. Callers convert to their transport format.
/// </summary>
public sealed record CommandResult(string Text, bool IsError)
{
    public static CommandResult Success(string text) => new(text, false);
    public static CommandResult Error(string text) => new(text, true);
}
