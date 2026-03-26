using ConsoleAppFramework;
using RepoQL.Commands;
using RepoQL.Client.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using Spectre.Console;
using RepoQL.Client.Helpers;
using RepoQL.Client.Commands;

namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// CLI verbs for the seven core MCP tools: query, command, explore, explain, read, import, execute.
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
    /// Execute DuckDB SQL against the indexed repository.
    /// </summary>
    /// <param name="sql">Short inline SQL. Omit to read from stdin, which is the preferred path for multi-line queries.</param>
    /// <param name="file">Path to a .sql file. Use for saved queries.</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Query(
        [Argument] string? sql = null,
        string? file = null,
        int budget = 15_000,
        CancellationToken cancel = default)
    {
        var queryText = await ResolveTextInputAsync(
            sql,
            file,
            "SQL query",
            "SQL is required. Pipe it on stdin, use --file path/to/query.sql, or pass a short inline query.")
            .ConfigureAwait(false);

        var parsed = CommandParser.TryParse(queryText);
        if (parsed != null)
            throw new ArgumentException("`repoql query` only accepts SQL. Use `repoql command ...` for imperative commands.");

        var result = await queryExecutor.ExecuteAsync(queryText, int.MaxValue, ResultFormat.Toon, budget, cancel)
            .ConfigureAwait(false);

        var output = result.Lines.Length > 0
            ? string.Join(Environment.NewLine, result.Lines)
            : "No results.";

        console.WriteLine(output);
    }

    /// <summary>
    /// Execute JavaScript in the RepoQL sandbox.
    /// </summary>
    /// <param name="intent">What the script is trying to accomplish. Used to guide summarization when output exceeds budget.</param>
    /// <param name="file">Path to a JavaScript file to execute. Omit to read the script from stdin.</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="timeout">Execution timeout in milliseconds.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Execute(
        [Argument] string intent,
        string? file = null,
        int budget = 15_000,
        int timeout = 300_000,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("Intent cannot be empty.");

        var script = await ResolveTextInputAsync(
            null,
            file,
            "JavaScript code",
            "JavaScript code is required. Pipe it on stdin or use --file path/to/script.js.")
            .ConfigureAwait(false);
        var result = await queryExecutor.ExecuteCodeAsync(
                script,
                intent,
                budget,
                timeout,
                ResultFormat.Toon,
                cancellationToken: cancel)
            .ConfigureAwait(false);

        var output = result.Lines.Length > 0
            ? string.Join(Environment.NewLine, result.Lines)
            : "No output. The script returned null or undefined.";

        if (result.SandboxError)
        {
            WriteError(output);
            return;
        }

        console.WriteLine(output);
    }

    /// <summary>
    /// Search the repository. The first argument is usually your search terms.
    /// </summary>
    /// <param name="query">Search terms for the common case.</param>
    /// <param name="mode">Search posture: inventory (wide scan), locate (default), or inspect (deeper readout).</param>
    /// <param name="breadth">Optional explicit breadth 1-10. Defaults from mode: inventory=8, locate=5, inspect=2.</param>
    /// <param name="question">Natural-language reranking question when you want results shaped around a specific ask.</param>
    /// <param name="budget">Token budget for response size.</param>
    /// <param name="uri">Scope filter. Relative paths like src/** are accepted and normalized to file:/// URIs.</param>
    /// <param name="boost">Regex to elevate matches (e.g., "(?i)interface|abstract").</param>
    /// <param name="penalize">Regex to demote matches (e.g., "(?i)test|mock").</param>
    /// <param name="limit">Max results to show.</param>
    /// <param name="wait">Wait for scope to be ready before executing.</param>
    /// <param name="force">Execute immediately, accepting partial results from an unready scope.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Explore(
        [Argument] string? query = null,
        string? mode = null,
        int? breadth = null,
        string? question = null,
        int budget = 2000,
        string? uri = null,
        string? boost = null,
        string? penalize = null,
        int? limit = null,
        bool wait = false,
        bool force = false,
        CancellationToken cancel = default)
    {
        var request = ResolveExploreRequest(query, mode, breadth, question);
        var normalizedUri = CliSyntax.NormalizeCliUriExpression(uri);

        var readiness = ParseReadiness(wait, force);
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        var response = await client.ExploreAsync(
            budget, request.Breadth, normalizedUri, request.Keywords, boost, penalize, limit, question,
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
    /// <param name="uri">Scope filter. Relative paths like src/Auth/** are accepted and normalized to file:/// URIs.</param>
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
            var normalizedUri = CliSyntax.NormalizeCliUriExpression(uri);

            var response = await client.ExplainAsync(question, normalizedUri, budget, keywords,
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
    /// <param name="target">URI, relative path, or glob (e.g., "src/Foo.cs", "src/**").</param>
    /// <param name="symbol">Optional symbol selector appended as #symbol=...</param>
    /// <param name="line">Optional line selector appended as #line=42 or #line=42,60.</param>
    /// <param name="chars">Optional char selector appended as #char=100,150.</param>
    /// <param name="tree">Directory tree view: folders, files, or headlines.</param>
    /// <param name="structure">Show signatures without bodies.</param>
    /// <param name="headline">Show one-line summaries per file.</param>
    /// <param name="history">Show git history for the target.</param>
    /// <param name="historyQuery">Optional query to rank history results.</param>
    /// <param name="blame">Show git blame for the target.</param>
    /// <param name="changes">Show working copy changes for the target.</param>
    /// <param name="lint">Show lint diagnostics for the target.</param>
    /// <param name="lintLevel">Optional lint level such as errors or warnings.</param>
    /// <param name="find">Semantic search within the target scope.</param>
    /// <param name="similar">Find content similar to the given seed URI.</param>
    /// <param name="grep">Literal text search within the target scope.</param>
    /// <param name="regex">Regular expression search within the target scope.</param>
    /// <param name="question">Ask a focused question about the target scope.</param>
    /// <param name="budget">Token budget — controls depth (headline/structure/full).</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Read(
        [Argument] string target,
        string? symbol = null,
        string? line = null,
        string? chars = null,
        string? tree = null,
        bool structure = false,
        bool headline = false,
        bool history = false,
        string? historyQuery = null,
        bool blame = false,
        bool changes = false,
        bool lint = false,
        string? lintLevel = null,
        string? find = null,
        string? similar = null,
        string? grep = null,
        string? regex = null,
        string? question = null,
        int budget = 5000,
        CancellationToken cancel = default)
    {
        var normalizedUri = CliSyntax.BuildReadExpression(
            target,
            symbol,
            line,
            chars,
            tree,
            structure,
            headline,
            history,
            historyQuery,
            blame,
            changes,
            lint,
            lintLevel,
            find,
            similar,
            grep,
            regex,
            question);
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        if (normalizedUri.Contains("=> find:", StringComparison.OrdinalIgnoreCase) ||
            normalizedUri.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = ExtractBaseUri(normalizedUri);
            await WaitForScopeReadyAsync(client, baseUri, cancel).ConfigureAwait(false);
        }

        var response = await client.ReadAsync(normalizedUri, budget, cancel).ConfigureAwait(false);

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
    /// <param name="uri">URI to import (e.g., github://owner/repo) or a local path. Prefix with '-' to remove.</param>
    /// <param name="analyze">Run full analysis on the imported source so symbol-level views are available immediately.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Import(
        [Argument] string uri,
        bool analyze = false,
        CancellationToken cancel = default)
    {
        var normalizedImportUri = CliSyntax.NormalizeCliImportUri(uri);
        var isRemoval = normalizedImportUri.TrimStart().StartsWith('-');
        var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
        ImportResult? result = null;

        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                isRemoval ? "Removing import..." : analyze ? "Importing and analyzing..." : "Importing repository...",
                async _ =>
            {
                result = await client.ImportRepositoryAsync(normalizedImportUri.Trim(), analyze, cancellationToken: cancel).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (isRemoval)
        {
            console.MarkupLine($"[green]Removed:[/] {Markup.Escape(normalizedImportUri.Trim().TrimStart('-'))}");
            return;
        }

        if (result is null)
            throw new InvalidOperationException("Import completed without a result.");

        if (!string.IsNullOrWhiteSpace(result.Message))
            console.WriteLine(result.Message);
        else
            console.MarkupLine($"[green]Imported:[/] {Markup.Escape(normalizedImportUri.Trim())}");

        if (!string.IsNullOrWhiteSpace(result.OperationId))
        {
            console.WriteLine($"Operation ID: {result.OperationId}");
            console.WriteLine("Check progress with:");
            console.WriteLine($"  repoql query \"SELECT * FROM _operation('{result.OperationId}')\"");
            console.WriteLine("  repoql query \"SELECT * FROM _operations()\"");
            return;
        }

        if (result.TotalFiles > 0)
        {
            var summary = $"Files ready: {result.IndexedCount}/{result.TotalFiles} indexed";
            if (result.EmbeddedCount > 0)
                summary += $", {result.EmbeddedCount} embedded";
            if (result.FailedCount > 0)
                summary += $", {result.FailedCount} failed";

            console.WriteLine(summary);
        }
    }

    private async Task WaitForScopeReadyAsync(IRepoQlClient client, string? scope, CancellationToken cancel)
    {
        var status = await client.GetScopeReadinessAsync(scope, cancel).ConfigureAwait(false);
        if (status.IsReady)
            return;

        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Waiting for indexing... {status.ReadyPercent}% complete", async ctx =>
            {
                while (!cancel.IsCancellationRequested)
                {
                    status = await client.GetScopeReadinessAsync(scope, cancel).ConfigureAwait(false);
                    if (status.IsReady)
                        return;

                    ctx.Status($"Waiting for indexing... {status.ReadyPercent}% complete");
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

    private static async Task<string> ResolveTextInputAsync(
        string? inlineText,
        string? file,
        string subject,
        string missingMessage)
    {
        if (!string.IsNullOrWhiteSpace(inlineText) && !string.IsNullOrWhiteSpace(file))
            throw new ArgumentException($"Provide {subject} inline, via --file, or on stdin — not multiple sources.");

        if (!string.IsNullOrWhiteSpace(file))
        {
            var filePath = Path.GetFullPath(file);
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"{subject} file not found: {filePath}", filePath);

            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(inlineText))
            return inlineText;

        if (Console.IsInputRedirected)
        {
            var stdin = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin))
                return stdin;
        }

        throw new ArgumentException(missingMessage);
    }

    private static ExploreCliRequest ResolveExploreRequest(
        string? query,
        string? mode,
        int? breadth,
        string? question)
    {
        var trimmedQuery = query?.Trim();
        var normalizedMode = NormalizeExploreMode(mode);
        var effectiveBreadth = breadth ?? normalizedMode switch
        {
            "inventory" => 8,
            "inspect" => 2,
            _ => 5
        };

        if (string.IsNullOrWhiteSpace(trimmedQuery) && string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Explore needs search terms or --question.");

        if (effectiveBreadth is < 1 or > 10)
            throw new ArgumentException($"Invalid breadth '{effectiveBreadth}'. Use a value between 1 and 10.");

        return new ExploreCliRequest(effectiveBreadth, trimmedQuery);
    }

    private static string NormalizeExploreMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => "locate",
            "inventory" or "survey" or "scan" => "inventory",
            "locate" or "find" or "search" => "locate",
            "inspect" or "detail" or "deep" => "inspect",
            var invalid => throw new ArgumentException($"Unknown explore mode '{invalid}'. Use inventory, locate, or inspect.")
        };

    private readonly record struct ExploreCliRequest(int Breadth, string? Keywords);

    private static string? ExtractBaseUri(string uri)
    {
        var modifierIndex = uri.IndexOf("=>", StringComparison.Ordinal);
        return modifierIndex <= 0 ? uri.Trim() : uri[..modifierIndex].Trim();
    }
}
