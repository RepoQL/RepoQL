namespace RepoQL.Formats.DotNet.TreeSitter;

/// <summary>
/// Identifies which query group a pattern belongs to within <see cref="CSharpQueries.CombinedQuery"/>.
/// Used to dispatch matches from a single combined query execution into typed buckets.
/// </summary>
public enum CSharpPatternGroup
{
    UsingDirectives,
    NamespaceDeclarations,
    ClassDeclarations,
    StructDeclarations,
    RecordDeclarations,
    InterfaceDeclarations,
    EnumDeclarations,
    MethodDeclarations,
    ConstructorDeclarations,
    PropertyDeclarations,
    FieldDeclarations,
    EventDeclarations,
    IndexerDeclarations,
    Comments
}
