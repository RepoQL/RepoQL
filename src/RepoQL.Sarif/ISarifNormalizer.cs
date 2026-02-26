using System.Text.Json;
using RepoQL.Sarif.Models;

namespace RepoQL.Sarif;

/// <summary>
/// Normalizes SARIF producer output into a stable, import-ready shape.
/// </summary>
public interface ISarifNormalizer
{
    /// <summary>
    /// Normalize a SARIF document for a repository root.
    /// The implementation must not throw for malformed SARIF payloads.
    /// </summary>
    NormalizationResult Normalize(JsonDocument sarif, string repoRootPath);
}
