using System.Text;
using RepoQL.Commands;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: List all registered commands.
/// Complexity: Reads the registry, formats a table.
/// </summary>
[CommandClass]
internal sealed class HelpCommand(CommandRegistry registry)
{
    [Command("?", Description = "List all commands")]
    public CommandResult Execute()
    {
        var commands = registry.GetCommands();
        if (commands.Count == 0)
            return CommandResult.Success("No commands registered.");

        var sb = new StringBuilder();
        sb.AppendLine("Available commands:");

        var maxLen = commands.Keys.Max(k => k.Length) + 2; // +2 for "::"
        foreach (var (name, reg) in commands.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var padded = $"::{name}".PadRight(maxLen + 4);
            var desc = reg.Attribute.Description ?? "";
            sb.AppendLine($"  {padded} {desc}");
        }

        sb.AppendLine();
        sb.Append("Use ::command --help for usage details.");

        return CommandResult.Success(sb.ToString());
    }
}
