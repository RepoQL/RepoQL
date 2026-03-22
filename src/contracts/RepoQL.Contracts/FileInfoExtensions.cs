using Microsoft.Extensions.FileProviders;

namespace RepoQL.Contracts;

/// <summary>
/// Extension methods for IFileInfo to simplify content-based file type detection.
/// </summary>
public static class FileInfoExtensions
{
    /// <summary>
    /// Reads and returns the first non-empty line from the file.
    /// </summary>
    /// <returns>The first non-whitespace line, or null if file is empty/unreadable</returns>
    public static async Task<string?> GetFirstLineAsync(
        this IFileInfo fileInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);

        try
        {
            await using var stream = fileInfo.CreateReadStream();
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return line;
            }
        }
        catch
        {
            // Ignore read errors
        }

        return null;
    }

    /// <summary>
    /// Checks if the first non-empty line starts with any of the given prefixes (after trimming).
    /// </summary>
    /// <example>
    /// if (await artifact.File.FirstLineStartsWith(["#", "---"], ct))
    ///     artifact.MediaType = MarkdownMediaType;
    /// </example>
    public static async Task<bool> FirstLineStartsWith(
        this IFileInfo fileInfo,
        string[] prefixes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefixes);

        var firstLine = await fileInfo.GetFirstLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(firstLine))
            return false;

        var trimmed = firstLine.TrimStart();
        return prefixes.Any(prefix =>
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the first non-empty line contains any of the given strings.
    /// </summary>
    public static async Task<bool> FirstLineContains(
        this IFileInfo fileInfo,
        string[] patterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var firstLine = await fileInfo.GetFirstLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(firstLine))
            return false;

        return patterns.Any(pattern =>
            firstLine.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the first N non-empty lines from the file.
    /// </summary>
    public static async Task<string[]> GetFirstLinesAsync(
        this IFileInfo fileInfo,
        int lineCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);
        if (lineCount <= 0)
            return [];

        var lines = new List<string>(lineCount);

        try
        {
            await using var stream = fileInfo.CreateReadStream();
            using var reader = new StreamReader(stream);

            while (lines.Count < lineCount &&
                   await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }
        catch
        {
            // Return what we got
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Checks if any of the first N non-empty lines start with any of the given prefixes.
    /// </summary>
    /// <example>
    /// // Check if any of first 3 lines starts with SQL keywords
    /// if (await artifact.File.AnyLineStartsWith(["SELECT", "INSERT", "UPDATE"], 3, ct))
    ///     artifact.MediaType = SqlMediaType;
    /// </example>
    public static async Task<bool> AnyLineStartsWith(
        this IFileInfo fileInfo,
        string[] prefixes,
        int lineCount = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefixes);

        var lines = await fileInfo.GetFirstLinesAsync(lineCount, cancellationToken).ConfigureAwait(false);

        return lines.Any(line =>
        {
            var trimmed = line.TrimStart();
            return prefixes.Any(prefix =>
                trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        });
    }
}
