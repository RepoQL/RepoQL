namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Provides the set of annotation source identifiers that an analyzer may emit for the current document.
/// </summary>
public interface IAnnotationSourceProvider
{
    IEnumerable<string> GetAnalyzerSources(DocumentModel document, AnalyzerContext context);
}
