using Spectre.Console;
using RepoQL.Contracts;

namespace RepoQL.Cli.Formatters;

internal static class SummariesSpectreFormatter
{
    public static void Render(GetDocumentSummariesResponse response)
    {
        foreach (var res in response.Results)
        {
            if (!string.IsNullOrEmpty(res.Error))
            {
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(res.Uri)}[/]");
                AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(res.Error)}\n");
                continue;
            }

            // Header
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(res.Uri)}[/]");

            if (res.Status == SummaryStatus.NotFound)
            {
                AnsiConsole.MarkupLine("[dim](document not found)[/]\n");
                continue;
            }

            if (res.Annotations.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim](no annotations)[/]\n");
                continue;
            }

            // Prefer outline first, then others
            var anns = res.Annotations
                .OrderBy(a => a.Kind.Equals("outline", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Kind)
                .ToList();

            foreach (var a in anns)
            {
                var kindTag = a.Kind.Equals("outline", StringComparison.OrdinalIgnoreCase)
                    ? "[green]outline[/]"
                    : $"[yellow]{Markup.Escape(a.Kind)}[/]";

                // Print message compactly; avoid repeating URI if it's first line
                var msg = a.Message ?? string.Empty;
                var lines = msg.Replace("\r\n", "\n").Split('\n');
                var start = 0;
                if (lines.Length > 0 && string.Equals(lines[0].Trim(), res.Uri, StringComparison.OrdinalIgnoreCase))
                    start = 1;

                // Tag header line for kind when not outline
                if (!a.Kind.Equals("outline", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"  {kindTag}");
                }

                for (var i = start; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Escape Spectre markup
                    AnsiConsole.WriteLine(EscapeNoMarkup(line));
                }
                AnsiConsole.WriteLine();
            }
        }
    }

    private static string EscapeNoMarkup(string s)
    {
        // Do not interpret markup. Use Spectre's escape for brackets.
        return s.Replace("[", "[[").Replace("]", "]]");
    }
}
