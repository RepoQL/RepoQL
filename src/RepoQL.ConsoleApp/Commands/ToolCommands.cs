using ConsoleAppFramework;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// CLI verbs for the six core MCP tools: query, command, explore, explain, read, import.
///
/// Purpose: Lets humans use the same capabilities from the terminal that agents
/// use via MCP — same gRPC calls, same rendered output, no MCP client required.
///
/// Complexity: Thin wrappers over the gRPC client. MCP-specific patterns
/// (orientation nudge, "call again to wait", budget-exceeded-repeat) are
/// replaced with CLI-appropriate behavior (spinners, print-everything).
/// </summary>
[RegisterCommands]
internal class ToolCommands(
    IAnsiConsole console,
    QueryExecutor queryExecutor,
    RepoQlClientProvider clientProvider,
    CommandRegistry commandRegistry,
    SelfTestRunner selfTestRunner)
{
    /// <summary>
    /// Run an imperative command such as diagnostics, config, or queue management.
    /// </summary>
    /// <param name="command">Command to run (e.g. "diagnostics.fast", "config", "?").</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Command(
        [Argument] string command,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty.");

        var input = command.Trim();
        if (!input.StartsWith("::", StringComparison.Ordinal))
            input = $"::{input}";

        var parsed = CommandParser.TryParse(input);
        if (parsed == null)
            throw new ArgumentException("Could not parse command. Use `repoql command ?` to list available commands.");

        commandRegistry.DiscoverCommands();
        var result = await commandRegistry.ExecuteAsync(parsed, cancel).ConfigureAwait(false);
        WriteCommandResult(result);
    }

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
        var parsed = CommandParser.TryParse(sql);
        if (parsed != null)
        {
            commandRegistry.DiscoverCommands();
            var cmdResult = await commandRegistry.ExecuteAsync(parsed, cancel).ConfigureAwait(false);
            WriteCommandResult(cmdResult);
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
    /// <param name="wait">Wait for scope to be ready before executing.</param>
    /// <param name="force">Execute immediately, accepting partial results from an unready scope.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Explore(
        [Argument] string breadth,
        string? keywords = null,
        int budget = 2000,
        string? uri = null,
        string? boost = null,
        string? penalize = null,
        int? limit = null,
        bool wait = false,
        bool force = false,
        CancellationToken cancel = default)
    {
        if (!int.TryParse(breadth, out var breadthValue) || breadthValue < 1 || breadthValue > 10)
        {
            throw new ArgumentException(
                $"Invalid breadth '{breadth}'. Usage: explore <breadth 1-10> [keywords] [--budget N] [--uri GLOB] [--boost REGEX] [--penalize REGEX] [--limit N]. " +
                "Suggested mapping: inventory=8, locate=5, inspect=2.");
        }

        var readiness = ParseReadiness(wait, force);
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        var response = await client.ExploreAsync(
            budget, breadthValue, uri, keywords, boost, penalize, limit,
            readiness: readiness, cancellationToken: cancel)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            WriteError(response.Error);
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
    /// <param name="keywords">Search keywords — code identifiers, class names, synonyms. Skips LLM keyword extraction.</param>
    /// <param name="wait">Wait for scope to be ready before executing.</param>
    /// <param name="force">Execute immediately, accepting partial results from an unready scope.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Explain(
        [Argument] string question,
        int budget = 2000,
        string? uri = null,
        string? keywords = null,
        bool wait = false,
        bool force = false,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.");

        try
        {
            var readiness = ParseReadiness(wait, force);
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

            var response = await client.ExplainAsync(question, uri, budget, keywords,
                readiness: readiness, cancellationToken: cancel).ConfigureAwait(false);
            if (!response.Success)
            {
                WriteError(response.Error);
                return;
            }

            console.WriteLine(response.RenderedOutput);
        }
        catch (Exception ex)
        {
            await WriteExplainErrorAsync(ex, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Read content from the repository with progressive disclosure.
    /// </summary>
    /// <param name="uri">URI, glob, or modifier (e.g., "file:///src/Foo.cs", "file:///src/** =&gt; tree: folders").</param>
    /// <param name="budget">Token budget — controls depth (headline/structure/full).</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Read(
        [Argument] string uri,
        int budget = 5000,
        CancellationToken cancel = default)
    {
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        if (uri.Contains("=> find:", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = ExtractBaseUri(uri);
            await WaitForScopeReadyAsync(client, baseUri, cancel).ConfigureAwait(false);
        }

        var response = await client.ReadAsync(uri, budget, cancel).ConfigureAwait(false);

        if (!response.Success)
        {
            WriteError(response.Error);
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
            console.MarkupLine($"[green]Removed:[/] {Markup.Escape(uri.Trim().TrimStart('-'))}");
        else
            console.MarkupLine($"[green]Imported:[/] {Markup.Escape(uri.Trim())}");
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

    private async Task WriteExplainErrorAsync(Exception ex, CancellationToken cancel)
    {
        var cleanMessage = ErrorClassifier.GetCleanMessage(ex);

        if (ex is RepoQlDiagnosticsException diagnosticsException)
        {
            WriteError($"{cleanMessage}\n\n{diagnosticsException.Diagnostics}");
            return;
        }

        if (ErrorClassifier.IsInfrastructureError(ex))
        {
            var diagnostics = await selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
            WriteError($"{cleanMessage}\n\n{diagnostics}");
            return;
        }

        WriteError(cleanMessage);
    }

    private void WriteCommandResult(CommandResult result)
    {
        if (result.IsError)
            WriteError(result.Text);
        else
            console.WriteLine(result.Text);
    }

    private static ScopeReadinessMode ParseReadiness(bool wait, bool force) => (wait, force) switch
    {
        (_, true) => ScopeReadinessMode.Force,
        (true, _) => ScopeReadinessMode.Wait,
        _ => ScopeReadinessMode.None
    };

    private void WriteError(string message)
        => console.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    private static string? ExtractBaseUri(string uri)
    {
        var modifierIndex = uri.IndexOf("=>", StringComparison.Ordinal);
        return modifierIndex <= 0 ? uri.Trim() : uri[..modifierIndex].Trim();
    }
}
