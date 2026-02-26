using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Sarif.Models;
using AnnotationSpan = RepoQL.Contracts.Models.Span;

namespace RepoQL.Sarif;

/// <summary>
/// Purpose: Orchestrate SARIF import from file to source-wide annotation replacement.
/// Complexity: Handles normalization errors, location resolution, semantic key computation, and source aggregation.
/// </summary>
public sealed class SarifImportService : ISarifImportService
{
    private const string AnnotationKind = "lint";
    private const string UnresolvedDocumentUriText = "repoql:///sarif/unresolved";
    private static readonly Guid UnresolvedDocumentId = Guid.Parse("f5c197d8-7efd-4f49-bce4-5d07111f74d8");
    private static readonly RepoUri UnresolvedDocumentUri = RepoUri.Parse(UnresolvedDocumentUriText);

    private readonly ISarifNormalizer _normalizer;
    private readonly DuckDbDataStore _store;
    private readonly string _repoRootPath;
    private readonly ILogger<SarifImportService>? _logger;

    public SarifImportService(
        ISarifNormalizer normalizer,
        DuckDbDataStore store,
        string repoRootPath,
        ILogger<SarifImportService>? logger = null)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repoRootPath = string.IsNullOrWhiteSpace(repoRootPath)
            ? throw new ArgumentException("repoRootPath is required.", nameof(repoRootPath))
            : Path.GetFullPath(repoRootPath);
        _logger = logger;
    }

    public async Task<SarifImportResult> ImportAsync(
        string sarifFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sarifFilePath);

        var resolvedPath = ResolveInputPath(sarifFilePath);
        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException($"SARIF file not found at {resolvedPath}");

        using var sarif = await LoadSarifAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var normalization = _normalizer.Normalize(sarif, _repoRootPath);
        if (normalization.Runs.Count == 0)
        {
            var fatalMessage = normalization.Warnings.FirstOrDefault()
                ?? "SARIF import failed: no valid runs were produced.";
            throw new InvalidOperationException(fatalMessage);
        }

        var warnings = new List<string>(normalization.Warnings);
        var bySource = new Dictionary<string, SourceAggregation>(StringComparer.Ordinal);
        var documentCache = new Dictionary<string, Node?>(StringComparer.Ordinal);
        Guid? unresolvedDocumentId = null;

        for (var runIndex = 0; runIndex < normalization.Runs.Count; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var run = normalization.Runs[runIndex];
            var source = string.IsNullOrWhiteSpace(run.Source) ? "unknown" : run.Source;
            if (!bySource.TryGetValue(source, out var aggregation))
            {
                aggregation = new SourceAggregation(source, LoadExistingSpansForSource(source));
                bySource[source] = aggregation;
            }

            for (var resultIndex = 0; resultIndex < run.Results.Count; resultIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedResult = run.Results[resultIndex];
                try
                {
                    ProcessNormalizedResult(
                        aggregation,
                        normalizedResult,
                        runIndex,
                        documentCache,
                        ref unresolvedDocumentId);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Run {runIndex} result {resultIndex} was skipped: {ex.Message}");
                    _logger?.LogWarning(
                        ex,
                        "Failed to import SARIF result (run={RunIndex}, result={ResultIndex}, source={Source})",
                        runIndex,
                        resultIndex,
                        source);
                }
            }
        }

        var perSource = new List<SourceImportResult>(bySource.Count);
        foreach (var source in bySource.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var aggregate = bySource[source];
            var replaceResult = _store.ReplaceAnnotationsBySource(
                source,
                AnnotationKind,
                aggregate.Annotations,
                aggregate.Spans);

            var total = aggregate.SemanticKeys.Count;
            var unchanged = Math.Max(0, total - replaceResult.Inserted - replaceResult.Updated);

            perSource.Add(new SourceImportResult(
                Source: source,
                Total: total,
                New: replaceResult.Inserted,
                Updated: replaceResult.Updated,
                Unchanged: unchanged,
                Expired: replaceResult.Expired,
                Resolved: aggregate.Resolved,
                Unresolved: aggregate.Unresolved));
        }

        var totalFindings = perSource.Sum(s => s.Total);
        if (totalFindings == 0)
        {
            warnings.Add(
                "SARIF import contained zero findings after normalization. Existing findings for participating sources were expired.");
        }

        return new SarifImportResult(
            Sources: perSource,
            TotalFindings: totalFindings,
            ResolvedToFiles: perSource.Sum(s => s.Resolved),
            UnresolvedPaths: perSource.Sum(s => s.Unresolved),
            Warnings: warnings);
    }

    private static async Task<JsonDocument> LoadSarifAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in SARIF file at {path}: {ex.Message}", ex);
        }
    }

    private void ProcessNormalizedResult(
        SourceAggregation source,
        NormalizedResult result,
        int runIndex,
        IDictionary<string, Node?> documentCache,
        ref Guid? unresolvedDocumentId)
    {
        var normalizedPath = NormalizePath(result.NormalizedPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            throw new InvalidOperationException("normalizedPath was empty.");

        var startLine = result.Region?.StartLine ?? 0;
        var severity = MapSeverity(result.Level);
        var semanticKey = ComputeSemanticKey(
            source.Source,
            result.RuleId,
            normalizedPath,
            startLine,
            result.Message,
            result.PartialFingerprints,
            result.Fingerprints);

        var data = BuildPayload(source.Source, runIndex, result);
        var resolvedDocument = ResolveDocument(normalizedPath, documentCache, out var repoRelativeUri);

        Guid scopeDocumentId;
        Guid? targetSpanId = null;
        RepoUri? targetUri = null;

        if (resolvedDocument is not null)
        {
            scopeDocumentId = resolvedDocument.Id;
            source.Resolved++;

            if (result.Region?.StartLine is > 0)
            {
                if (source.ExistingSpanByKey.TryGetValue(semanticKey, out var existingSpanId) && existingSpanId.HasValue)
                {
                    targetSpanId = existingSpanId.Value;
                }
                else
                {
                    var span = CreateSpan(resolvedDocument.Id, result.Region);
                    source.Spans.Add(span);
                    targetSpanId = span.Id;
                }
            }
        }
        else
        {
            scopeDocumentId = unresolvedDocumentId ??= EnsureUnresolvedDocument();
            source.Unresolved++;
            targetUri = BuildUnresolvedTargetUri(
                normalizedPath,
                startLine,
                repoRelativeUri);
        }

        var annotation = new Annotation
        {
            SemanticKey = semanticKey,
            Kind = AnnotationKind,
            Severity = severity,
            Source = source.Source,
            RuleId = result.RuleId,
            Message = result.Message,
            Data = data,
            ScopeDocumentId = scopeDocumentId,
            TargetSpanId = targetSpanId,
            TargetUri = targetUri
        };

        source.Annotations.Add(annotation);
        source.SemanticKeys.Add(semanticKey);
    }

    private Guid EnsureUnresolvedDocument()
    {
        var existing = _store.GetDocumentByUri(UnresolvedDocumentUri);
        if (existing is not null)
            return existing.Id;

        var unresolved = new Node
        {
            Id = UnresolvedDocumentId,
            Kind = "document",
            Uri = UnresolvedDocumentUri,
            Headline = "Unresolved SARIF findings",
            Props = new JsonObject
            {
                ["synthetic"] = true,
                ["source"] = "sarif"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return _store.UpsertDocumentByUri(UnresolvedDocumentUri, unresolved).Id;
    }

    private Node? ResolveDocument(
        string normalizedPath,
        IDictionary<string, Node?> cache,
        out RepoUri? repoRelativeUri)
    {
        repoRelativeUri = null;
        if (!TryCreateRepoRelativeUri(normalizedPath, out var uri))
            return null;

        repoRelativeUri = uri;
        var cacheKey = uri.AbsoluteUri;
        if (cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var node = _store.GetDocumentByUri(uri);
        cache[cacheKey] = node;
        return node;
    }

    private static AnnotationSpan CreateSpan(Guid documentId, NormalizedRegion region)
    {
        return new AnnotationSpan
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            StartLine = region.StartLine,
            StartColumn = region.StartColumn,
            EndLine = region.EndLine,
            EndColumn = region.EndColumn
        };
    }

    private static RepoUri? BuildUnresolvedTargetUri(
        string normalizedPath,
        int startLine,
        RepoUri? repoRelativeUri)
    {
        if (repoRelativeUri is not null)
        {
            var line = startLine > 0 ? startLine : 0;
            return RepoUri.FromLines(repoRelativeUri.Container, line, null);
        }

        if (RepoUri.TryParse(normalizedPath, out var target))
            return target;

        var externalAsFileUri = ConvertToFileUri(normalizedPath);
        return RepoUri.TryParse(externalAsFileUri, out target) ? target : null;
    }

    private static string ComputeSemanticKey(
        string source,
        string ruleId,
        string normalizedPath,
        int startLine,
        string message,
        IReadOnlyDictionary<string, string>? partialFingerprints,
        IReadOnlyDictionary<string, string>? fingerprints)
    {
        var fingerprint = SelectFingerprint(partialFingerprints)
                          ?? SelectFingerprint(fingerprints)
                          ?? ComputeFallbackFingerprint(ruleId, normalizedPath, startLine, message);

        return $"{source}:{ruleId}:{normalizedPath}:{startLine}:{fingerprint}";
    }

    private static string? SelectFingerprint(IReadOnlyDictionary<string, string>? fingerprints)
    {
        if (fingerprints is null || fingerprints.Count == 0)
            return null;

        foreach (var item in fingerprints.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(item.Value))
                return item.Value.Trim();
        }

        return null;
    }

    private static string ComputeFallbackFingerprint(string ruleId, string path, int startLine, string message)
    {
        var payload = $"{ruleId}:{path}:{startLine}:{message}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string MapSeverity(string level)
    {
        return (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "error" => "error",
            "warning" => "warning",
            "note" => "info",
            "none" => "hint",
            _ => "warning"
        };
    }

    private static JsonObject BuildPayload(string source, int runIndex, NormalizedResult result)
    {
        var payload = new JsonObject
        {
            ["sarif_source"] = source,
            ["sarif_run_index"] = runIndex,
            ["original_level"] = ExtractOriginalLevel(result)
        };

        if (result.RuleMetadata is not null)
            payload["rule"] = result.RuleMetadata.DeepClone();

        if (result.PartialFingerprints is { Count: > 0 })
            payload["partialFingerprints"] = ToJsonObject(result.PartialFingerprints);

        if (result.Fingerprints is { Count: > 0 })
            payload["fingerprints"] = ToJsonObject(result.Fingerprints);

        if (result.Data is not null)
        {
            foreach (var entry in result.Data)
            {
                if (string.Equals(entry.Key, "originalLevel", StringComparison.Ordinal))
                    continue;

                if (!payload.ContainsKey(entry.Key))
                    payload[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return payload;
    }

    private static string ExtractOriginalLevel(NormalizedResult result)
    {
        if (result.Data is not null
            && result.Data.TryGetPropertyValue("originalLevel", out var originalLevelNode)
            && TryGetNodeString(originalLevelNode, out var originalLevel))
        {
            return originalLevel!;
        }

        return result.Level;
    }

    private static bool TryGetNodeString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is not JsonValue scalar)
            return false;

        if (!scalar.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
            return false;

        value = text;
        return true;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var kvp in values.OrderBy(k => k.Key, StringComparer.Ordinal))
            obj[kvp.Key] = kvp.Value;
        return obj;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private static bool TryCreateRepoRelativeUri(string normalizedPath, out RepoUri uri)
    {
        uri = null!;
        var path = NormalizePath(normalizedPath);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.Contains("://", StringComparison.Ordinal))
            return false;

        if (path.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            return false;

        var encodedPath = string.Join(
            "/",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        var uriText = $"file:///{encodedPath}";
        return RepoUri.TryParse(uriText, out uri);
    }

    private static string ConvertToFileUri(string normalizedPath)
    {
        var path = NormalizePath(normalizedPath);
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            var encodedAbsolute = string.Join(
                "/",
                path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            return $"file:///{encodedAbsolute}";
        }

        var windowsAbsolute = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
        if (windowsAbsolute)
        {
            var drive = path[..2];
            var remainder = path.Length > 2 ? path[2..].TrimStart('/') : string.Empty;
            var encodedRemainder = string.Join(
                "/",
                remainder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            return string.IsNullOrEmpty(encodedRemainder)
                ? $"file:///{drive}/"
                : $"file:///{drive}/{encodedRemainder}";
        }

        var encodedRelative = string.Join(
            "/",
            path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"file:///{encodedRelative}";
    }

    private string ResolveInputPath(string sarifFilePath)
    {
        var candidate = sarifFilePath.Trim();
        if (Path.IsPathRooted(candidate))
            return Path.GetFullPath(candidate);

        return Path.GetFullPath(Path.Combine(_repoRootPath, candidate));
    }

    private IReadOnlyDictionary<string, Guid?> LoadExistingSpansForSource(string source)
    {
        var escapedSource = source.Replace("'", "''", StringComparison.Ordinal);
        var rows = _store.Read(
            $"""
             SELECT semantic_key, target_span_id
             FROM annotation
             WHERE source = '{escapedSource}'
               AND kind = '{AnnotationKind}'
               AND semantic_key IS NOT NULL
             """,
            r => new
            {
                SemanticKey = r.GetString(0),
                TargetSpanId = r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1)
            });

        return rows.ToDictionary(
            r => r.SemanticKey,
            r => r.TargetSpanId,
            StringComparer.Ordinal);
    }

    private sealed class SourceAggregation(string source, IReadOnlyDictionary<string, Guid?> existingSpanByKey)
    {
        public string Source { get; } = source;
        public List<Annotation> Annotations { get; } = [];
        public List<AnnotationSpan> Spans { get; } = [];
        public HashSet<string> SemanticKeys { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, Guid?> ExistingSpanByKey { get; } = existingSpanByKey;
        public int Resolved { get; set; }
        public int Unresolved { get; set; }
    }
}
