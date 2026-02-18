using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Configuration;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Purpose: Runs a find-style semantic narrowing pass over Inspect candidates.
/// Complexity: Uses adaptive chunk widening with bounded timeout and maps results
/// back to document-level snippet evidence for Explore rendering.
/// </summary>
internal sealed class InspectRefinementService(DuckDbDataStore db, RepoQlConfig config) : IInspectRefinementService
{
    private const int InspectRefinementTotalTimeoutMs = 40_000;

    private readonly RepoQlConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly FindRefinementEngine _engine = new(db);

    public Task<InspectRefinementResult> RefineAsync(
        string keywords,
        IReadOnlyList<InspectRefinementCandidate> candidates,
        int tokenBudget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keywords) || tokenBudget <= 0 || candidates.Count == 0)
        {
            return Task.FromResult(new InspectRefinementResult(
                Results: [],
                Rounds: 0,
                Widenings: 0,
                FinalCandidateLimit: 0,
                FallbackUsed: false,
                TimedOut: false,
                DegradedReason: null));
        }

        var candidateByDocument = candidates
            .GroupBy(c => ToDocumentUri(c.Uri), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Confidence).First(),
                StringComparer.OrdinalIgnoreCase);

        var documentUris = candidateByDocument.Keys
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .ToList();

        if (documentUris.Count == 0)
        {
            return Task.FromResult(new InspectRefinementResult(
                Results: [],
                Rounds: 0,
                Widenings: 0,
                FinalCandidateLimit: 0,
                FallbackUsed: false,
                TimedOut: false,
                DegradedReason: null));
        }

        var settings = FindRuntimeSettings.From(
            _config.Find,
            totalTimeoutOverrideMs: InspectRefinementTotalTimeoutMs);

        var outcome = _engine.ExecuteAdaptiveSearch(keywords.Trim(), documentUris, settings, cancellationToken);

        var refined = outcome.Results
            .Where(r => r.Score >= settings.MinScoreThreshold)
            .Select(r =>
            {
                candidateByDocument.TryGetValue(ToDocumentUri(r.Uri), out var candidate);
                var headline = !string.IsNullOrWhiteSpace(r.Headline)
                    ? r.Headline
                    : candidate?.Headline;

                return new InspectRefinedSnippet(
                    Uri: r.Uri,
                    Headline: headline,
                    Snippet: r.Snippet,
                    LineStart: r.LineStart,
                    LineEnd: r.LineEnd,
                    Lang: candidate?.Lang ?? r.Lang,
                    Score: r.Score);
            })
            .ToList();

        return Task.FromResult(new InspectRefinementResult(
            Results: refined,
            Rounds: outcome.Rounds,
            Widenings: outcome.Widenings,
            FinalCandidateLimit: outcome.FinalCandidateLimit,
            FallbackUsed: outcome.FallbackUsed,
            TimedOut: outcome.TimedOut,
            DegradedReason: outcome.DegradedReason));
    }

    private static string ToDocumentUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return uri;

        if (RepoUri.TryParse(uri, out var parsed))
            return parsed.Container.AbsoluteUri;

        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? uri[..hash] : uri;
    }
}
