using System.Runtime.CompilerServices;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Contracts;

public interface IFormatAnalyzer
{
    bool Supports(SemanticMediaType mediaType);

    IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, CancellationToken cancellationToken = default);

    async IAsyncEnumerable<AnalysisResult> AnalyzeEmbeddedAsync(EmbeddedFragment fragment, AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
