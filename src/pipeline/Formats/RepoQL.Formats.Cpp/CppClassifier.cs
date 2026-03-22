using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Cpp;

/// <summary>
/// Classifies C/C++ source artifacts into C/C++ semantic kinds.
///
/// Purpose: Route C-family files to the C/C++ parser with stable kind metadata.
///
/// Complexity: Extension mapping plus .h sibling/content sniffing.
/// </summary>
public sealed class CppClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    private static readonly Regex CppIndicatorRegex = new(
        @"\bclass\b|\bnamespace\b|\btemplate\b|\busing\s+namespace\b|#\s*include\s*<iostream>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        try
        {
            var extension = Path.GetExtension(item.Name).ToUpperInvariant();
            switch (extension)
            {
                case ".C":
                    return (CppMediaTypes.C, PipelineResult.Success);
                case ".CPP":
                case ".CC":
                case ".CXX":
                    return (CppMediaTypes.Cpp, PipelineResult.Success);
                case ".HPP":
                case ".HH":
                case ".HXX":
                    return (CppMediaTypes.CppHeader, PipelineResult.Success);
                case ".IPP":
                case ".TPP":
                case ".INL":
                    return (CppMediaTypes.CppInline, PipelineResult.Success);
                case ".H":
                    return (await ClassifyDotHAsync(item, token).ConfigureAwait(false), PipelineResult.Success);
                default:
                    return await next(item).ConfigureAwait(false);
            }
        }
        catch
        {
            return (null, PipelineResult.Error);
        }
    }

    private static async Task<SemanticMediaType> ClassifyDotHAsync(IDiscoveredArtifact item, CancellationToken token)
    {
        if (HasCppSibling(item.PhysicalPath))
        {
            return CppMediaTypes.CppHeader;
        }

        try
        {
            await using var stream = item.CreateReadStream();
            using var reader = new StreamReader(stream);

            for (var i = 0; i < 100; i++)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (CppIndicatorRegex.IsMatch(line))
                {
                    return CppMediaTypes.CppHeader;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Fallback to extension-only behavior for .h on I/O read issues.
            return CppMediaTypes.C;
        }

        return CppMediaTypes.C;
    }

    private static bool HasCppSibling(string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(physicalPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(physicalPath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, $"{stem}.cpp"))
               || File.Exists(Path.Combine(directory, $"{stem}.cc"))
               || File.Exists(Path.Combine(directory, $"{stem}.cxx"));
    }
}
