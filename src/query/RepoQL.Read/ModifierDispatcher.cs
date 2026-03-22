using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Read;

/// <summary>
/// Purpose: Parses modifier syntax, routes to handlers, and enforces budget confirmation.
/// Complexity: Centralizes dispatch and repeat-to-confirm caching so handlers stay focused on content.
/// </summary>
public sealed class ModifierDispatcher
{
    /// <summary>
    /// Small overages (within 15%) pass through without requiring a repeat-to-confirm round-trip.
    /// Only overages beyond this threshold trigger the confirmation gate.
    /// </summary>
    private const double BudgetToleranceFactor = 1.15;

    private const int CacheSeconds = 60;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(CacheSeconds);
    private static readonly ConcurrentDictionary<string, ModifierDispatchCacheEntry> Cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IModifierHandler> _handlers;
    private readonly IReadContentProvider _contentProvider;

    public ModifierDispatcher(IReadContentProvider contentProvider, IEnumerable<IModifierHandler>? handlers)
    {
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
        _handlers = handlers?.ToList() ?? [];
    }

    public async Task<ReadExecutionResult?> TryExecuteAsync(
        string input,
        int tokenBudget,
        TrustSignal status,
        CancellationToken cancellationToken,
        Stopwatch? stopwatch = null)
    {
        if (!TryParse(input, tokenBudget, out var request, out var error))
            return null;

        if (error is not null)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: error);
        }

        if (request is null)
            return null;

        var handler = ResolveHandler(request.Modifier);
        if (handler is null)
        {
            var available = FormatAvailableModifiers();
            var message = string.IsNullOrEmpty(available)
                ? $"Unknown modifier '{request.Modifier}'. No modifiers are registered."
                : $"Unknown modifier '{request.Modifier}'. Available modifiers: {available}.";

            return new ReadExecutionResult(
                Success: false,
                Error: message);
        }

        var cacheKey = BuildCacheKey(request);
        if (TryGetCachedResult(cacheKey, out var cachedResult))
        {
            return BuildReadExecutionResult(
                request,
                cachedResult,
                status,
                stopwatch,
                budgetOverridden: true);
        }

        var documents = await _contentProvider.FetchGlobAsync(request.Pattern, cancellationToken).ConfigureAwait(false);

        if (documents.Count == 0)
        {
            var diagnostic = await NoMatchDiagnostics.DiagnoseAsync(request.Pattern, _contentProvider, status, cancellationToken).ConfigureAwait(false);
            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: diagnostic,
                Representation: request.Modifier,
                FilesRead: 0,
                FilesOmitted: 0);
        }

        ModifierResult result;
        try
        {
            result = await handler.ExecuteAsync(documents, request.Parameter, tokenBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ReadExecutionResult(
                Success: false,
                Error: $"Modifier '{request.Modifier}' failed: {ex.Message}");
        }

        var toleranceBudget = (int)(tokenBudget * BudgetToleranceFactor);
        var exceedsBudget = result.TokenCount > toleranceBudget;
        if (exceedsBudget)
        {
            Cache[cacheKey] = new ModifierDispatchCacheEntry(result, DateTimeOffset.UtcNow.Add(CacheDuration));
            var confirmation = FormatBudgetConfirmationMessage(result, tokenBudget);
            return new ReadExecutionResult(
                Success: true,
                RenderedOutput: confirmation,
                Representation: request.Modifier,
                FilesRead: 0,
                FilesOmitted: result.TotalAvailable);
        }

        return BuildReadExecutionResult(request, result, status, stopwatch, budgetOverridden: false);
    }

    private static ReadExecutionResult BuildReadExecutionResult(
        ParsedReadRequest request,
        ModifierResult result,
        TrustSignal status,
        Stopwatch? stopwatch,
        bool budgetOverridden)
    {
        var statusWithTiming = status with { ExecutionTimeMs = stopwatch?.ElapsedMilliseconds ?? 0 };
        var hint = budgetOverridden
            ? $"budget override: {FormatTokenCount(result.TokenCount)} > {FormatTokenCount(request.TokenBudget)}"
            : null;
        var footer = RepresentationFormatter.FormatStatusFooter(statusWithTiming, result.TokenCount, hint);

        var output = new StringBuilder(result.Content ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(result.Metadata.Warning))
        {
            if (output.Length > 0)
                output.Append('\n');
            output.Append(result.Metadata.Warning);
        }

        if (output.Length > 0)
            output.Append('\n');
        output.Append(footer);

        var filesOmitted = Math.Max(0, result.TotalAvailable - result.Shown);
        return new ReadExecutionResult(
            Success: true,
            RenderedOutput: output.ToString(),
            Representation: request.Modifier,
            FilesRead: result.Shown,
            FilesOmitted: filesOmitted);
    }

    private static bool TryGetCachedResult(string cacheKey, out ModifierResult result)
    {
        result = default!;

        if (!Cache.TryGetValue(cacheKey, out var entry))
            return false;

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Cache.TryRemove(cacheKey, out _);
            return false;
        }

        result = entry.Result;
        return true;
    }

    private static string FormatBudgetConfirmationMessage(ModifierResult result, int tokenBudget)
    {
        var matched = result.TotalAvailable > 0
            ? $"{result.TotalAvailable} item{(result.TotalAvailable == 1 ? string.Empty : "s")} matched."
            : "Results exceed the token budget.";

        return $"Results would use {FormatTokenCount(result.TokenCount)} (budget: {FormatTokenCount(tokenBudget)}).\n" +
               $"{matched}\nRepeat request to proceed.";
    }

    private IModifierHandler? ResolveHandler(string modifier)
        => _handlers.FirstOrDefault(handler => handler.CanHandle(modifier));

    private string FormatAvailableModifiers()
    {
        return string.Join(", ",
            _handlers
                .Select(handler => handler.ModifierName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryParse(
        string input,
        int tokenBudget,
        out ParsedReadRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        var separatorIndex = input.IndexOf("=>", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return false;

        var pattern = input[..separatorIndex].Trim();
        var remainder = input[(separatorIndex + 2)..].Trim();

        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(remainder))
        {
            error = "Invalid modifier syntax. Use: <pattern> => <modifier>[: <param>]";
            return true;
        }

        var modifier = remainder;
        string? parameter = null;

        var paramIndex = remainder.IndexOf(':', StringComparison.Ordinal);
        if (paramIndex >= 0)
        {
            modifier = remainder[..paramIndex].Trim();
            parameter = remainder[(paramIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(parameter))
                parameter = null;
        }

        if (string.IsNullOrWhiteSpace(modifier))
        {
            error = "Invalid modifier syntax. Use: <pattern> => <modifier>[: <param>]";
            return true;
        }

        request = new ParsedReadRequest(pattern, modifier, parameter, tokenBudget);
        return true;
    }

    private static string BuildCacheKey(ParsedReadRequest request)
    {
        var key = $"{request.Pattern}\n{request.Modifier}\n{request.Parameter}\n{request.TokenBudget}";
        return ComputeHash(key);
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string FormatTokenCount(int tokens)
    {
        return tokens switch
        {
            < 1000 => $"{tokens} tok",
            < 10000 => $"{tokens / 1000.0:F1}k tok",
            _ => $"{tokens / 1000.0:F0}k tok"
        };
    }
}

/// <summary>
/// Purpose: Captures parsed modifier input for consistent dispatch.
/// Complexity: Keeps parsing output together so handler selection stays deterministic.
/// </summary>
public sealed record ParsedReadRequest(
    string Pattern,
    string Modifier,
    string? Parameter,
    int TokenBudget);

/// <summary>
/// Purpose: Defines the contract for read modifier handlers.
/// Complexity: Isolates modifier logic behind a minimal async surface.
/// </summary>
public interface IModifierHandler
{
    string ModifierName { get; }

    bool CanHandle(string? modifier);

    Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct);
}

/// <summary>
/// Purpose: Standardizes handler output and token accounting for dispatch.
/// Complexity: Bundles counts and metadata so the dispatcher can enforce budgets.
/// </summary>
public sealed record ModifierResult(
    string Content,
    int TokenCount,
    int TotalAvailable,
    int Shown,
    bool ExceedsBudget,
    ResultMetadata Metadata);

/// <summary>
/// Purpose: Provides handler metadata for warnings and diagnostics.
/// Complexity: Centralizes optional context in a structured payload.
/// </summary>
public sealed record ResultMetadata(
    IReadOnlyList<string> FilesConsulted,
    string? Warning,
    IReadOnlyDictionary<string, object> Extra);

/// <summary>
/// Purpose: Stores cached modifier results for repeat-to-confirm.
/// Complexity: Tracks expiration so cache lifetime stays bounded.
/// </summary>
internal sealed record ModifierDispatchCacheEntry(
    ModifierResult Result,
    DateTimeOffset ExpiresAt);

