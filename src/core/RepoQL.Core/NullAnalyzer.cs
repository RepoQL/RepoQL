using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Core;

public sealed class NullAnalyzer(SemanticMediaType type) : IFormatAnalyzer
{
    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.ToString(), type.ToString(), StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, CancellationToken cancellationToken = default)
        => EmptyAsync();

    private static async IAsyncEnumerable<AnalysisResult> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}
