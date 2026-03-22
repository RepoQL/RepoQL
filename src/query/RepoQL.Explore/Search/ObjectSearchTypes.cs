namespace RepoQL.Explore.Search;

/// <summary>
/// Pre-computed query signals for efficient reuse across documents.
/// Created once at the start of object search and passed through the pipeline.
/// </summary>
public sealed class NormalizedQuerySignals
{
    public required string RawQuery { get; init; }
    public required float[]? QueryEmbedding { get; init; }
    public required QueryIntent DetectedIntent { get; init; }
}

/// <summary>
/// Detected query intent affects document expansion and scoring behavior.
/// </summary>
public enum QueryIntent
{
    /// <summary>Natural language question - favor semantic search, sharper doc selection.</summary>
    Semantic,
    /// <summary>Symbol-like query (PascalCase, dotted path) - favor lexical matching, broader doc selection.</summary>
    Symbol,
    /// <summary>Mixed/ambiguous query - balanced approach.</summary>
    Hybrid
}

/// <summary>
/// Candidate for JIT embedding enrichment. Created from ExploreCandidate for objects
/// whose semantic evidence is uncertain (inherited or missing).
/// Mutable scores allow in-place updates during the JIT embedding pipeline.
///
/// Purpose: Hold object metadata and JIT planning state during enrichment.
/// Complexity: Provenance-based uncertainty, expected-value JIT selection,
///   embedding storage, cosine similarity scoring.
/// </summary>
public sealed class ObjectCandidate
{
    public required string NodeId { get; init; }
    public required string Uri { get; init; }
    public required string DocumentUri { get; init; }
    public required string Kind { get; init; }
    public required string? Symbol { get; init; }
    public required string? Headline { get; init; }
    public required string? Structure { get; init; }

    public required int LineStart { get; init; }
    public required int LineEnd { get; init; }
    public required string? Lang { get; init; }
    public required string? SemanticType { get; init; }

    /// <summary>
    /// Semantic provenance from _explore_candidates: direct, chunk_overlap, inherited, none.
    /// Used for uncertainty-based JIT embedding selection.
    /// </summary>
    public string? SemProvenance { get; init; }

    // Cheap scores (computed without JIT embedding)
    public double ChunkOverlapScore { get; set; }
    public double CheapAggregateScore { get; set; }

    // JIT embedding planning
    public double Uncertainty { get; set; }
    public double ExpectedImpact { get; set; }
    public double ExpectedValue { get; set; }
    public bool SelectedForJitEmbedding { get; set; }

    // Final scores (after JIT embedding if selected)
    public float[]? JitEmbedding { get; set; }
    public double SemanticScore { get; set; }
    public int Confidence { get; set; }
}
