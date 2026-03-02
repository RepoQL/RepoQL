namespace RepoQL.Formats.Go.TreeSitter;

/// <summary>
/// Identifies which query group a <see cref="TreeSitter.QueryMatch.PatternIndex"/> belongs to
/// within <see cref="GoQueries.CombinedQuery"/>.
/// Used to dispatch matches from a single combined query execution into typed buckets.
/// </summary>
public enum GoPatternGroup
{
    PackageClause,
    ImportSpecs,
    StructDeclarations,
    StructFields,
    InterfaceDeclarations,
    InterfaceMethods,
    EmbeddedInterfaces,
    FunctionDeclarations,
    MethodDeclarations,
    TypeDefinitions,
    ConstantSpecs,
    VariableSpecs,
    Comments,
    GoStatements,
    ChannelTypes,
    SelectStatements
}
