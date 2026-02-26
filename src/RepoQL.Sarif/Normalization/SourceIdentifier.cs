using System.Text.RegularExpressions;

namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Resolves stable source slugs from SARIF producer names.
/// </summary>
public sealed class SourceIdentifier
{
    private static readonly Regex NonAlphanumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Resolve a source slug for the provided producer name.
    /// </summary>
    public string Resolve(string? producerName)
    {
        if (string.IsNullOrWhiteSpace(producerName))
            return "unknown";

        if (ProducerMap.Values.TryGetValue(producerName, out var known))
            return known;

        var slug = NonAlphanumeric.Replace(producerName.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}
