using RepoQL.Contracts.Analysis;

namespace RepoQL.Core.Analysis;

public interface IAnalysisResultWriter
{
        Task WriteAsync(
            string containerUri,
            IReadOnlyList<AnalysisResult> results,
            IReadOnlyCollection<string>? analyzerSources = null,
            CancellationToken cancellationToken = default);
}
