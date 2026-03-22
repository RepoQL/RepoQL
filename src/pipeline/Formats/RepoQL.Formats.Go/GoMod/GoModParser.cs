namespace RepoQL.Formats.Go.GoMod;

/// <summary>
/// Parses go.mod and go.work metadata from text using directive-oriented line scanning.
///
/// Purpose: Extract module/workspace metadata without depending on tree-sitter.
///
/// Complexity: Handles block directives, inline comments, and malformed-line recovery.
/// </summary>
internal sealed class GoModParser
{
    public GoModInfo Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? modulePath = null;
        string? goVersion = null;
        string? toolchain = null;
        var requirements = new List<GoModRequirement>();
        var replacements = new List<GoModReplacement>();
        var retractions = new List<GoModRetraction>();
        var uses = new List<GoModUse>();

        var blockDirective = BlockDirective.None;
        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (blockDirective != BlockDirective.None)
            {
                if (line.StartsWith(")", StringComparison.Ordinal))
                {
                    blockDirective = BlockDirective.None;
                    continue;
                }

                ParseBlockLine(blockDirective, line, requirements, replacements, retractions, uses);
                continue;
            }

            if (TryGetDirectiveRemainder(line, "module", out var moduleRemainder))
            {
                if (TryGetFirstToken(moduleRemainder, out var parsedModulePath))
                {
                    modulePath = parsedModulePath;
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "go", out var goRemainder))
            {
                if (TryGetFirstToken(goRemainder, out var parsedGoVersion))
                {
                    goVersion = parsedGoVersion;
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "toolchain", out var toolchainRemainder))
            {
                if (TryGetFirstToken(toolchainRemainder, out var parsedToolchain))
                {
                    toolchain = parsedToolchain;
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "require", out var requireRemainder))
            {
                if (IsBlockStart(requireRemainder))
                {
                    blockDirective = BlockDirective.Require;
                }
                else
                {
                    ParseRequirementLine(requireRemainder, requirements);
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "replace", out var replaceRemainder))
            {
                if (IsBlockStart(replaceRemainder))
                {
                    blockDirective = BlockDirective.Replace;
                }
                else
                {
                    ParseReplacementLine(replaceRemainder, replacements);
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "retract", out var retractRemainder))
            {
                if (IsBlockStart(retractRemainder))
                {
                    blockDirective = BlockDirective.Retract;
                }
                else
                {
                    ParseRetractionLine(retractRemainder, retractions);
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "use", out var useRemainder))
            {
                if (IsBlockStart(useRemainder))
                {
                    blockDirective = BlockDirective.Use;
                }
                else
                {
                    ParseUseLine(useRemainder, uses);
                }

                continue;
            }

            if (TryGetDirectiveRemainder(line, "exclude", out var excludeRemainder))
            {
                if (IsBlockStart(excludeRemainder))
                {
                    blockDirective = BlockDirective.Exclude;
                }

                continue;
            }
        }

        return new GoModInfo(
            modulePath,
            goVersion,
            toolchain,
            requirements,
            replacements,
            retractions,
            uses);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static void ParseBlockLine(
        BlockDirective blockDirective,
        string line,
        List<GoModRequirement> requirements,
        List<GoModReplacement> replacements,
        List<GoModRetraction> retractions,
        List<GoModUse> uses)
    {
        switch (blockDirective)
        {
            case BlockDirective.Require:
                ParseRequirementLine(line, requirements);
                break;
            case BlockDirective.Replace:
                ParseReplacementLine(line, replacements);
                break;
            case BlockDirective.Retract:
                ParseRetractionLine(line, retractions);
                break;
            case BlockDirective.Use:
                ParseUseLine(line, uses);
                break;
            case BlockDirective.Exclude:
            case BlockDirective.None:
            default:
                break;
        }
    }

    private static void ParseRequirementLine(string line, List<GoModRequirement> requirements)
    {
        var (content, comment) = SplitInlineComment(line);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var tokens = SplitTokens(content);
        if (tokens.Length < 2)
        {
            return;
        }

        var modulePath = TrimQuotes(tokens[0]);
        var version = TrimQuotes(tokens[1]);
        if (string.IsNullOrWhiteSpace(modulePath) || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        var isIndirect = !string.IsNullOrWhiteSpace(comment)
                         && comment.Contains("indirect", StringComparison.OrdinalIgnoreCase);
        requirements.Add(new GoModRequirement(modulePath, version, isIndirect));
    }

    private static void ParseReplacementLine(string line, List<GoModReplacement> replacements)
    {
        var (content, _) = SplitInlineComment(line);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var separator = content.IndexOf("=>", StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }

        var left = content[..separator].Trim();
        var right = content[(separator + 2)..].Trim();
        if (left.Length == 0 || right.Length == 0)
        {
            return;
        }

        var leftTokens = SplitTokens(left);
        var rightTokens = SplitTokens(right);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return;
        }

        var oldPath = TrimQuotes(leftTokens[0]);
        var oldVersion = leftTokens.Length > 1 ? TrimQuotes(leftTokens[1]) : null;
        var newPath = TrimQuotes(rightTokens[0]);
        var newVersion = rightTokens.Length > 1 ? TrimQuotes(rightTokens[1]) : null;
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        replacements.Add(new GoModReplacement(
            oldPath,
            string.IsNullOrWhiteSpace(oldVersion) ? null : oldVersion,
            newPath,
            string.IsNullOrWhiteSpace(newVersion) ? null : newVersion,
            IsLocalPath(newPath)));
    }

    private static void ParseRetractionLine(string line, List<GoModRetraction> retractions)
    {
        var (content, comment) = SplitInlineComment(line);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        string? low = null;
        string? high = null;

        var trimmed = content.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = trimmed.IndexOf("]", StringComparison.Ordinal);
            if (closingBracket > 0)
            {
                var range = trimmed[1..closingBracket];
                var rangeTokens = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (rangeTokens.Length > 0)
                {
                    low = TrimQuotes(rangeTokens[0]);
                    high = TrimQuotes(rangeTokens.Length > 1 ? rangeTokens[1] : rangeTokens[0]);
                }
            }
        }
        else
        {
            var tokens = SplitTokens(trimmed);
            if (tokens.Length > 0)
            {
                low = TrimQuotes(tokens[0]);
                high = low;
            }
        }

        if (string.IsNullOrWhiteSpace(low) || string.IsNullOrWhiteSpace(high))
        {
            return;
        }

        retractions.Add(new GoModRetraction(
            low,
            high,
            string.IsNullOrWhiteSpace(comment) ? null : comment));
    }

    private static void ParseUseLine(string line, List<GoModUse> uses)
    {
        var (content, _) = SplitInlineComment(line);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var tokens = SplitTokens(content);
        if (tokens.Length == 0)
        {
            return;
        }

        var path = TrimQuotes(tokens[0]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        uses.Add(new GoModUse(path));
    }

    private static bool TryGetDirectiveRemainder(string line, string directive, out string remainder)
    {
        remainder = string.Empty;
        if (!line.StartsWith(directive, StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Length == directive.Length)
        {
            return true;
        }

        var boundary = line[directive.Length];
        if (!char.IsWhiteSpace(boundary) && boundary != '(')
        {
            return false;
        }

        remainder = line[directive.Length..].TrimStart();
        return true;
    }

    private static bool TryGetFirstToken(string line, out string token)
    {
        token = string.Empty;
        var (content, _) = SplitInlineComment(line);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var tokens = SplitTokens(content);
        if (tokens.Length == 0)
        {
            return false;
        }

        token = TrimQuotes(tokens[0]);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool IsBlockStart(string remainder)
    {
        var (content, _) = SplitInlineComment(remainder);
        return content == "(";
    }

    private static (string Content, string? Comment) SplitInlineComment(string line)
    {
        var marker = line.IndexOf("//", StringComparison.Ordinal);
        if (marker < 0)
        {
            return (line.Trim(), null);
        }

        var content = line[..marker].Trim();
        var comment = line[(marker + 2)..].Trim();
        return (content, comment.Length == 0 ? null : comment);
    }

    private static string[] SplitTokens(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '`' && value[^1] == '`')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool IsLocalPath(string path)
    {
        if (path.StartsWith('.') || path.StartsWith('/'))
        {
            return true;
        }

        return !path.Contains('.', StringComparison.Ordinal);
    }

    private enum BlockDirective
    {
        None = 0,
        Require = 1,
        Replace = 2,
        Retract = 3,
        Use = 4,
        Exclude = 5
    }
}
