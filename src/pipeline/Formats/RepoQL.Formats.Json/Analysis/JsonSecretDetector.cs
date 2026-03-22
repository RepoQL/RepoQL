using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.Json.Analysis;

/// <summary>
/// Analyzer that emits lint warnings for potential hardcoded JSON secrets.
///
/// Purpose: Detect likely credentials in generic JSON values without modifying content.
///
/// Complexity: Applies key-name and value-pattern heuristics with placeholder filtering and line-aware targets.
/// </summary>
public sealed class JsonSecretDetector(ILogger<JsonSecretDetector>? logger = null) : IFormatAnalyzer
{
    private const string RuleId = "json.potential-secret";
    private const string Source = "RepoQL.Json";
    private const string ParseStateMetadataKey = "json.state";

    private static readonly string[] PlaceholderMarkers = ["TODO", "CHANGEME", "<", ">", "{", "}"];

    private readonly ILogger<JsonSecretDetector> _logger = logger ?? NullLogger<JsonSecretDetector>.Instance;

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.Kind?.StartsWith("json", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        DocumentModel document,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<AnalysisResult>();

        try
        {
            var state = document.GetMetadataOrDefault<JsonParseResult>(ParseStateMetadataKey);
            if (state is null)
                yield break;

            foreach (var key in state.Keys)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (key.ValueKind != JsonValueKind.String)
                    continue;

                var value = key.ScalarValue;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var normalizedValue = value.Trim();
                if (normalizedValue.Length == 0 || ContainsPlaceholderMarker(normalizedValue))
                    continue;

                if (!TryDetect(key.Name, normalizedValue, out var reason))
                    continue;

                var targetUri = RepoUri.FromLines(document.Uri.Container, key.StartLine, key.StartLine);
                var escapedPath = Uri.EscapeDataString(key.Path);

                findings.Add(new AnalysisResult
                {
                    SemanticKey = $"{document.Uri}#rule:{RuleId}@{escapedPath}:{key.StartLine}",
                    RuleId = RuleId,
                    Source = Source,
                    Kind = "lint",
                    Severity = AnalysisSeverity.Warning,
                    Message = $"Potential secret at '{key.Path}' ({reason}).",
                    Data = new JsonObject
                    {
                        ["path"] = key.Path,
                        ["key"] = key.Name,
                        ["start_line"] = key.StartLine
                    },
                    Target = new AnalysisTarget
                    {
                        TargetUri = targetUri
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JSON secret detection failed for {Uri}; returning no findings.", document.Uri);
            yield break;
        }

        foreach (var finding in findings)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            yield return finding;
        }

        await Task.CompletedTask;
    }

    private static bool TryDetect(string keyName, string value, out string reason)
    {
        if (SecretPatterns.TryMatchKeyName(keyName, out var keyPattern))
        {
            reason = $"key name contains '{keyPattern}'";
            return true;
        }

        if (value.Length >= 8 && SecretPatterns.TryMatchValuePrefix(value, out var prefix))
        {
            reason = $"value starts with '{prefix}'";
            return true;
        }

        if (value.Length >= 8 && SecretPatterns.LooksLikeBase64Secret(value))
        {
            reason = "value matches base64 secret pattern";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool ContainsPlaceholderMarker(string value)
    {
        foreach (var marker in PlaceholderMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
