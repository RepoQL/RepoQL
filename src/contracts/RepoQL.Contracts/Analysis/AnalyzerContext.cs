
namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Ambient services and configuration available to analyzers during execution.
/// </summary>
public sealed class AnalyzerContext(
    AnalyzerSettings settings,
    string repositoryPath)
{
    public AnalyzerSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public string RepositoryPath { get; } = repositoryPath ?? throw new ArgumentNullException(nameof(repositoryPath));
}
