using ConsoleAppFramework;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// CLI verbs for the five core MCP tools: query, explore, explain, read, import.
///
/// Purpose: Lets humans use the same capabilities from the terminal that agents
/// use via MCP — same gRPC calls, same rendered output, no MCP client required.
///
/// Complexity: Thin wrappers over the gRPC client. MCP-specific patterns
/// (orientation nudge, "call again to wait", budget-exceeded-repeat) are
/// replaced with CLI-appropriate behavior (spinners, print-everything).
/// </summary>
[RegisterCommands]
internal class ToolCommands(IAnsiConsole console, QueryExecutor queryExecutor, RepoQlClientProvider clientProvider, CommandRegistry commandRegistry)
{
    /// <summary>
    /// Execute a DuckDB SQL query or ::command against the indexed repository.
    /// </summary>
    /// <param name="sql">SQL or ::command to execute (e.g., ::diagnostics, ::diagnostics[fast]).</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Query(
        [Argument] string sql,
        int budget = 15_000,
        CancellationToken cancel = default)
    {
        // Command dispatch: ::name[params] syntax
        var parsed = CommandParser.TryParse(sql);
        if (parsed != null)
        {
            commandRegistry.DiscoverCommands();
            var cmdResult = await commandRegistry.ExecuteAsync(parsed, cancel);
            if (cmdResult.IsError)
                console.MarkupLine($"[red]{Markup.Escape(cmdResult.Text)}[/]");
            else
                console.WriteLine(cmdResult.Text);
            return;
        }

        var result = await queryExecutor.ExecuteAsync(sql, int.MaxValue, ResultFormat.Toon, budget, cancel)
            .ConfigureAwait(false);

        var output = result.Lines.Length > 0
            ? string.Join(Environment.NewLine, result.Lines)
            : "No results.";

        console.WriteLine(output);
    }

    /// <summary>
    /// Search and explore the repository with breadth-controlled discovery.
    /// </summary>
    /// <param name="breadth">Breadth 1-10 (1=depth, 10=coverage). Examples: inventory=8, locate=5, inspect=2.</param>
    /// <param name="keywords">Search terms — code words and synonyms (e.g., "login authentication", "cache").</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="uri">URI glob to filter scope (e.g., file:///src/**). Omit to search everywhere.</param>
    /// <param name="boost">Regex to elevate matches (e.g., "(?i)interface|abstract").</param>
    /// <param name="penalize">Regex to demote matches (e.g., "(?i)test|mock").</param>
    /// <param name="limit">Max results to show.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Explore(
        [Argument] string breadth,
        string? keywords = null,
        int budget = 2000,
        string? uri = null,
        string? boost = null,
        string? penalize = null,
        int? limit = null,
        CancellationToken cancel = default)
    {
        if (!int.TryParse(breadth, out var breadthValue) || breadthValue < 1 || breadthValue > 10)
        {
            throw new ArgumentException(
                $"Invalid breadth '{breadth}'. Usage: explore <breadth 1-10> [keywords] [--budget N] [--uri GLOB] [--boost REGEX] [--penalize REGEX] [--limit N]. " +
                "Suggested mapping: inventory=8, locate=5, inspect=2.");
        }

        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        // For keyword searches, wait for scope readiness with a spinner
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            await WaitForScopeReadyAsync(client, uri, cancel).ConfigureAwait(false);
        }

        var response = await client.ExploreAsync(
            budget, breadthValue, uri, keywords, boost, penalize, limit, cancel)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            console.MarkupLine($"[red]{Markup.Escape(response.Error)}[/]");
            return;
        }

        console.WriteLine(response.RenderedOutput);
    }

    /// <summary>
    /// Ask a question about the codebase and get a synthesized answer with citations.
    /// </summary>
    /// <param name="question">The question to answer (e.g., "How does authentication work?").</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="uri">URI glob to scope the search (e.g., file:///src/Auth/**). Omit to search everywhere.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Explain(
        [Argument] string question,
        int budget = 2000,
        string? uri = null,
        CancellationToken cancel = default)
    {
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
        await WaitForScopeReadyAsync(client, uri, cancel).ConfigureAwait(false);

        var response = await client.ExploreAsync(
            budget, 5, uri, question, null, null, null, cancel)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            console.MarkupLine($"[red]{Markup.Escape(response.Error)}[/]");
            return;
        }

        console.WriteLine(response.RenderedOutput);
    }

    /// <summary>
    /// Read content from the repository with progressive disclosure.
    /// </summary>
    /// <param name="uri">URI, glob, or modifier (e.g., "file:///src/Foo.cs", "file:///src/** =&gt; tree: folders").</param>
    /// <param name="budget">Token budget — controls depth (headline/structure/full).</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Read(
        [Argument] string uri,
        int budget = 3000,
        CancellationToken cancel = default)
    {
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        // For semantic modifiers, wait for scope readiness
        if (uri.Contains("=> find:", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = ExtractBaseUri(uri);
            await WaitForScopeReadyAsync(client, baseUri, cancel).ConfigureAwait(false);
        }

        var response = await client.ReadAsync(uri, budget, cancel).ConfigureAwait(false);

        if (!response.Success)
        {
            console.MarkupLine($"[red]{Markup.Escape(response.Error)}[/]");
            return;
        }

        console.WriteLine(response.RenderedOutput);
    }

    /// <summary>
    /// Import or remove an external repository (e.g., github://owner/repo).
    /// Prefix with '-' to remove.
    /// </summary>
    /// <param name="uri">URI to import (e.g., github://owner/repo). Prefix with '-' to remove.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Import(
        [Argument] string uri,
        CancellationToken cancel = default)
    {
        var isRemoval = uri.TrimStart().StartsWith('-');
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(isRemoval ? "Removing import..." : "Importing repository...", async _ =>
            {
                await client.ImportRepositoryAsync(uri.Trim(), cancellationToken: cancel).ConfigureAwait(false);
            });

        if (isRemoval)
        {
            console.MarkupLine($"[green]Removed:[/] {Markup.Escape(uri.Trim().TrimStart('-'))}");
        }
        else
        {
            console.MarkupLine($"[green]Imported:[/] {Markup.Escape(uri.Trim())}");
        }
    }

    private async Task WaitForScopeReadyAsync(IRepoQlClient client, string? scope, CancellationToken cancel)
    {
        var status = await client.GetScopeReadinessAsync(scope, cancel).ConfigureAwait(false);
        if (status.IsReady)
            return;

        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Waiting for indexing... {status.ReadyPercent}% ready", async ctx =>
            {
                while (!cancel.IsCancellationRequested)
                {
                    status = await client.GetScopeReadinessAsync(scope, cancel).ConfigureAwait(false);
                    if (status.IsReady)
                        return;

                    ctx.Status($"Waiting for indexing... {status.ReadyPercent}% ready");
                    await Task.Delay(500, cancel).ConfigureAwait(false);
                }
            });
    }

    private static string? ExtractBaseUri(string uri)
    {
        var modifierIndex = uri.IndexOf("=>", StringComparison.Ordinal);
        return modifierIndex <= 0 ? uri.Trim() : uri[..modifierIndex].Trim();
    }
}
