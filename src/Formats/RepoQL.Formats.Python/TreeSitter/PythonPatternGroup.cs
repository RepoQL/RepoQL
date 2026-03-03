namespace RepoQL.Formats.Python.TreeSitter;

/// <summary>
/// Identifies which query group a <see cref="TreeSitter.QueryMatch.PatternIndex"/> belongs to
/// within <see cref="PythonQueries.CombinedQuery"/>.
/// Used to dispatch matches from a single combined query execution into typed buckets.
/// </summary>
public enum PythonPatternGroup
{
    DecoratedDefinitions,
    ClassDeclarations,
    FunctionDeclarations,
    SelfAttributeAssignments,
    YieldSites,
    AsyncWithSites,
    AsyncForSites,
    ImportStatements,
    ImportFromStatements,
    TypeAliasStatements,
    MetaprogrammingCalls,
    DunderDefinitions,
    FrameworkFieldPatterns
}
