using System.Text.RegularExpressions;

namespace RepoQL.Xray.Search;

/// <summary>
/// Pre-computed query signals for efficient reuse across documents.
/// Created once at the start of object search and passed through the pipeline.
/// </summary>
public sealed class NormalizedQuerySignals
{
    public required string RawQuery { get; init; }
    public required float[]? QueryEmbedding { get; init; }
    public required IReadOnlyList<Regex> BoostPatterns { get; init; }
    public required Regex? NegativePattern { get; init; }
    public required IReadOnlySet<string> QueryTokensLower { get; init; }
    public required string BoostRegex { get; init; }
    public required QueryIntent DetectedIntent { get; init; }
    public required double SoftmaxTemperature { get; init; }
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
/// Document with computed expansion probability from softmax selection.
/// </summary>
public record DocumentExpansionCandidate(
    string DocumentUri,
    double DocumentScore,
    double SoftmaxProbability,
    double CumulativeProbability,
    string? Headline,
    string? Structure,
    string? Lang,
    string? SemanticType,
    string Source,
    double SemanticScore,
    double Bm25Score,
    int StructMentions,
    int BodyMentions,
    IReadOnlyList<ChunkScore> HighScoringChunks
);

/// <summary>
/// Cheap candidate from first pass (before JIT embedding).
/// Mutable scores allow in-place updates during the scoring pipeline.
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
    public required string? Body { get; init; }
    public required int LineStart { get; init; }
    public required int LineEnd { get; init; }
    public required int? StartByte { get; init; }
    public required int? EndByte { get; init; }
    public required string? Lang { get; init; }
    public required string? SemanticType { get; init; }

    // Cheap scores (computed without JIT embedding)
    public double RegexHitScore { get; set; }
    public double ChunkOverlapScore { get; set; }
    public double TypePriorScore { get; set; }
    public double NameHitScore { get; set; }
    public double CheapAggregateScore { get; set; }

    // JIT embedding planning
    public double Uncertainty { get; set; }
    public double ExpectedImpact { get; set; }
    public double ExpectedValue { get; set; }
    public bool SelectedForJitEmbedding { get; set; }

    // Final scores (after JIT embedding if selected)
    public float[]? JitEmbedding { get; set; }
    public double SemanticScore { get; set; }
    public double FinalScore { get; set; }
    public int Confidence { get; set; }
}

/// <summary>
/// Configuration for the enhanced object search algorithm.
/// </summary>
public sealed class ObjectSearchConfig
{
    /// <summary>Minimum cumulative probability mass to capture during document selection.</summary>
    public double MinProbabilityMass { get; init; } = 0.85;

    /// <summary>Maximum documents to expand regardless of probability.</summary>
    public int MaxDocumentsToExpand { get; init; } = 15;

    /// <summary>Minimum documents to expand (ensures we always try a few).</summary>
    public int MinDocumentsToExpand { get; init; } = 3;

    /// <summary>Maximum JIT embeddings to compute per search.</summary>
    public int MaxJitEmbeddings { get; init; } = 30;

    /// <summary>Expected value threshold for JIT embedding selection.</summary>
    public double JitEmbeddingThreshold { get; init; } = 0.15;

    /// <summary>Maximum objects to consider per expanded document.</summary>
    public int MaxObjectsPerDocument { get; init; } = 50;

    /// <summary>
    /// Weight coefficients for cheap candidate scoring.
    /// </summary>
    public CheapScoringWeights CheapWeights { get; init; } = new();

    /// <summary>
    /// Type priors for scoring (kind -> weight). Higher = more important object types.
    /// </summary>
    public IReadOnlyDictionary<string, double> TypePriors { get; init; } = new Dictionary<string, double>
    {
        // C# types
        ["cs_class"] = 1.1,
        ["cs_method"] = 1.2,
        ["cs_property"] = 0.9,
        ["cs_field"] = 0.8,
        ["cs_interface"] = 1.1,
        ["cs_enum"] = 0.9,

        // TypeScript types
        ["ts_function"] = 1.2,
        ["ts_class"] = 1.1,
        ["ts_interface"] = 1.0,
        ["ts_type"] = 0.9,

        // Markdown types
        ["md_heading"] = 0.8,
        ["md_code_block"] = 0.7,

        // GraphQL types
        ["graphql_type"] = 1.0,
        ["graphql_query"] = 1.1,
        ["graphql_mutation"] = 1.1,

        // SQL types
        ["sql_function"] = 1.1,
        ["sql_procedure"] = 1.1,
        ["sql_view"] = 1.0
    };

    /// <summary>Default type prior for unknown kinds.</summary>
    public double DefaultTypePrior { get; init; } = 1.0;

    /// <summary>
    /// Get temperature for softmax based on detected query intent.
    /// Lower temperature = sharper distribution (concentrate on top docs).
    /// </summary>
    public static double GetTemperature(QueryIntent intent) => intent switch
    {
        QueryIntent.Semantic => 0.3,  // Sharp - concentrate on top semantic matches
        QueryIntent.Symbol => 0.7,    // Broad - symbol might be in lower-ranked docs
        QueryIntent.Hybrid => 0.5,    // Middle ground
        _ => 0.5
    };

    /// <summary>
    /// Get final scoring weights based on query intent.
    /// </summary>
    public static FinalScoringWeights GetFinalWeights(QueryIntent intent) => intent switch
    {
        QueryIntent.Semantic => new(Semantic: 0.55, Name: 0.20, Regex: 0.15, Type: 0.10),
        QueryIntent.Symbol => new(Semantic: 0.25, Name: 0.45, Regex: 0.20, Type: 0.10),
        QueryIntent.Hybrid => new(Semantic: 0.40, Name: 0.30, Regex: 0.20, Type: 0.10),
        _ => new(Semantic: 0.40, Name: 0.30, Regex: 0.20, Type: 0.10)
    };
}

/// <summary>
/// Weight coefficients for cheap candidate scoring (before JIT embedding).
/// </summary>
public sealed class CheapScoringWeights
{
    public double NameHit { get; init; } = 0.35;
    public double ChunkOverlap { get; init; } = 0.30;
    public double RegexHit { get; init; } = 0.20;
    public double TypePrior { get; init; } = 0.15;
}

/// <summary>
/// Weight coefficients for final scoring (after JIT embedding).
/// </summary>
public record FinalScoringWeights(
    double Semantic,
    double Name,
    double Regex,
    double Type
);
