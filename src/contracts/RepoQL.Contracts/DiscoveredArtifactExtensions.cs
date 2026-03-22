namespace RepoQL.Contracts;

/// <summary>
/// Extension methods for DiscoveredArtifact to simplify common patterns in CanLoadAsync implementations.
/// </summary>
public static class DiscoveredArtifactExtensions
{
    /// <summary>
    /// Checks if the artifact's file name matches any of the given extensions (case-insensitive),
    /// and if so, sets the artifact's MediaType and returns true.
    /// </summary>
    /// <remarks>
    /// This should typically be the FIRST check in CanLoadAsync (highest priority).
    /// </remarks>
    /// <example>
    /// if (artifact.MatchesExtensions([".md", ".markdown"], MarkdownMediaType))
    ///     return true;
    /// </example>
    public static bool MatchesExtensions(
        this DiscoveredArtifact artifact,
        string[] extensions,
        SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(mediaType);

        if (string.IsNullOrEmpty(artifact.File.Name))
            return false;

        var fileName = artifact.File.Name.ToLowerInvariant();
        foreach (var ext in extensions)
        {
            if (fileName.EndsWith(ext.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                artifact.MediaType = mediaType;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the artifact already has a media type that matches the given type,
    /// and if so, ensures the kind is set correctly and returns true.
    /// </summary>
    /// <remarks>
    /// This should typically be the SECOND check in CanLoadAsync.
    /// </remarks>
    /// <example>
    /// if (artifact.HasMediaType(MarkdownMediaType))
    ///     return true;
    /// </example>
    public static bool HasMediaType(
        this DiscoveredArtifact artifact,
        SemanticMediaType expectedType)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(expectedType);

        if (artifact.MediaType is null)
            return false;

        // Check if kind matches
        if (string.Equals(artifact.MediaType.Kind, expectedType.Kind, StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = artifact.MediaType.WithKind(expectedType.Kind ?? artifact.MediaType.Kind);
            return true;
        }

        // Check if type/subtype matches
        if (string.Equals(artifact.MediaType.Type, expectedType.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artifact.MediaType.Subtype, expectedType.Subtype, StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = artifact.MediaType.WithKind(expectedType.Kind ?? artifact.MediaType.Kind);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if the artifact already has a SPECIFIC media type classification
    /// (not just generic text/plain).
    /// </summary>
    /// <remarks>
    /// Use this as a GUARD before running content-based heuristics to prevent
    /// overriding well-established type classifications.
    ///
    /// Returns true if the file has a specific classification like:
    /// - code.csharp
    /// - code.javascript
    /// - dotnet.csproj
    ///
    /// Returns false for generic classifications like:
    /// - null (no classification)
    /// - text/plain with no kind
    /// - text/plain;kind=plain.document
    /// </remarks>
    /// <example>
    /// // Only run content detection on unclassified files
    /// if (!artifact.HasSpecificMediaType())
    /// {
    ///     if (await artifact.File.FirstLineStartsWith(["#", "---"], ct))
    ///     {
    ///         artifact.MediaType = MarkdownMediaType;
    ///         return true;
    ///     }
    /// }
    /// </example>
    public static bool HasSpecificMediaType(this DiscoveredArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.MediaType is null)
            return false;

        if (string.IsNullOrEmpty(artifact.MediaType.Kind))
            return false;

        // "plain.document" is the generic fallback, so it's not specific
        if (string.Equals(artifact.MediaType.Kind, "plain.document", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
