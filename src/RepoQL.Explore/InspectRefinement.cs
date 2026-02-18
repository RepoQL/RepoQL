namespace RepoQL.Explore;

/// <summary>
/// Optional second-pass refinement for Inspect intent. Implementations can use
/// semantic narrowing (e.g., find/zoom pipelines) to surface method-body evidence.
/// </summary>
public interface IInspectRefinementService
{
    Task<InspectRefinementResult> RefineAsync(
        string keywords,
        IReadOnlyList<InspectRefinementCandidate> candidates,
        int tokenBudget,
        CancellationToken cancellationToken);
}

/// <summary>
/// Candidate document to refine after breadth-first Explore allocation.
/// </summary>
public sealed record InspectRefinementCandidate(
    string Uri,
    int Confidence,
    string? Headline,
    string? Lang);

/// <summary>
/// Refined evidence snippet for a document.
/// </summary>
public sealed record InspectRefinedSnippet(
    string Uri,
    string? Headline,
    string? Snippet,
    int? LineStart,
    int? LineEnd,
    string? Lang,
    double Score);

/// <summary>
/// Result from Inspect refinement pass.
/// </summary>
public sealed record InspectRefinementResult(
    IReadOnlyList<InspectRefinedSnippet> Results,
    int Rounds,
    int Widenings,
    int FinalCandidateLimit,
    bool FallbackUsed,
    bool TimedOut,
    string? DegradedReason);

/// <summary>
/// Controls whether and how Inspect refinement runs.
/// </summary>
public sealed record InspectRefinementOptions(
    bool Enabled = true,
    int BaseRefineBudgetPercent = 25,
    int MinRefineBudgetPercent = 15,
    int MaxRefineBudgetPercent = 40,
    int MaxDocumentsToRefine = 24,
    int HighConfidenceThreshold = 85,
    int MediumConfidenceThreshold = 70,
    int HighConfidenceSnippetsPerDocument = 3,
    int MediumConfidenceSnippetsPerDocument = 2,
    int LowConfidenceSnippetsPerDocument = 1);
