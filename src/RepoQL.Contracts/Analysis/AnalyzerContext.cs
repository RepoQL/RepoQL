
namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Ambient services and configuration available to analyzers during execution.
/// </summary>
public sealed class AnalyzerContext(
    AnalyzerSettings settings,
    string repositoryPath,
    IFormatRegistry formatRegistry,
    IAnalysisWorkspace workspace)
{
    public AnalyzerSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public string RepositoryPath { get; } = repositoryPath ?? throw new ArgumentNullException(nameof(repositoryPath));

    public IFormatRegistry Formats { get; } = formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));

    public IAnalysisWorkspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));
}
