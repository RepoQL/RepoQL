using RepoQL.Contracts.Analysis;

namespace RepoQL.Core.Analysis;

public sealed class NullAnalysisResultWriter : IAnalysisResultWriter
{
    public static readonly NullAnalysisResultWriter Instance = new();

    private NullAnalysisResultWriter() { }

        public Task WriteAsync(
            string containerUri,
            IReadOnlyList<AnalysisResult> results,
            IReadOnlyCollection<string>? analyzerSources = null,
            CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
