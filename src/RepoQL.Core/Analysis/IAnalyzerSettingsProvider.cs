using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis;

public interface IAnalyzerSettingsProvider
{
    AnalyzerSettings Resolve(string containerUri, SemanticMediaType media, Node documentNode);
}
