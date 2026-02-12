namespace RepoQL.Commands;

/// <summary>
/// Purpose: Parse <c>::name[param1, param2]</c> command syntax from query input.
/// Complexity: Span-based parser. Returns null for non-commands (SQL passthrough).
/// Malformed commands (e.g., unclosed bracket) return a parse error result, not null.
/// </summary>
public static class CommandParser
{
    /// <summary>
    /// Try to parse a command from input. Returns null if input is not a command.
    /// Returns a <see cref="ParsedCommand"/> with <see cref="ParsedCommand.ParseError"/>
    /// set if the command syntax is malformed.
    /// </summary>
    public static ParsedCommand? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.AsSpan().Trim();

        // Must start with ::
        if (!trimmed.StartsWith("::"))
            return null;

        // :: alone is not a command
        if (trimmed.Length <= 2)
            return null;

        var rest = trimmed[2..];

        // The name is everything up to '[', ' ', or end
        var nameEnd = rest.Length;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == '[' || rest[i] == ' ')
            {
                nameEnd = i;
                break;
            }
        }

        if (nameEnd == 0)
            return null;

        var name = rest[..nameEnd].ToString();

        // Validate name: alphanumeric, dots, hyphens, underscores
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
                return new ParsedCommand(name, [], ParseError: $"Invalid character '{c}' in command name.");
        }

        var afterName = rest[nameEnd..].Trim();

        if (afterName.IsEmpty)
            return new ParsedCommand(name, []);

        // Check for --help flag
        if (afterName.SequenceEqual("--help"))
            return new ParsedCommand(name, [], IsHelp: true);

        // Must be '[' for parameters
        if (afterName[0] != '[')
            return new ParsedCommand(name, [], ParseError: $"Unexpected content after command name: '{afterName.ToString()}'");

        // Find closing bracket
        var closeBracket = afterName.LastIndexOf(']');
        if (closeBracket < 0)
            return new ParsedCommand(name, [], ParseError: "Unclosed parameter bracket. Expected ']'.");

        var paramContent = afterName[1..closeBracket];

        // Check for --help after brackets
        var afterBracket = afterName[(closeBracket + 1)..].Trim();
        var isHelp = afterBracket.SequenceEqual("--help");

        if (paramContent.Trim().IsEmpty)
            return new ParsedCommand(name, [], IsHelp: isHelp);

        // Split on commas, trim each
        var parameters = SplitParams(paramContent);
        return new ParsedCommand(name, parameters, IsHelp: isHelp);
    }

    private static string[] SplitParams(ReadOnlySpan<char> content)
    {
        var results = new List<string>();
        var start = 0;

        for (var i = 0; i <= content.Length; i++)
        {
            if (i == content.Length || content[i] == ',')
            {
                var param = content[start..i].Trim();
                if (param.Length > 0)
                    results.Add(param.ToString());
                start = i + 1;
            }
        }

        return results.ToArray();
    }
}

/// <summary>
/// A parsed command with name, parameters, and optional help/error flags.
/// </summary>
public sealed record ParsedCommand(
    string Name,
    string[] Parameters,
    bool IsHelp = false,
    string? ParseError = null);
