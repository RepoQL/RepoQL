using System.Text.Json;
using System.Text.Json.Nodes;
using RepoQL.Sarif.Models;
using RepoQL.Sarif.Normalization;

namespace RepoQL.Sarif;

/// <summary>
/// Pure SARIF normalizer that absorbs producer variance and never throws for malformed payloads.
/// </summary>
public sealed class SarifNormalizer : ISarifNormalizer
{
    private readonly PathNormalizer _pathNormalizer;
    private readonly RuleCollector _ruleCollector;
    private readonly SeverityResolver _severityResolver;
    private readonly SourceIdentifier _sourceIdentifier;

    public SarifNormalizer()
        : this(
            new PathNormalizer(),
            new RuleCollector(),
            new SeverityResolver(),
            new SourceIdentifier())
    {
    }

    internal SarifNormalizer(
        PathNormalizer pathNormalizer,
        RuleCollector ruleCollector,
        SeverityResolver severityResolver,
        SourceIdentifier sourceIdentifier)
    {
        _pathNormalizer = pathNormalizer;
        _ruleCollector = ruleCollector;
        _severityResolver = severityResolver;
        _sourceIdentifier = sourceIdentifier;
    }

    public NormalizationResult Normalize(JsonDocument sarif, string repoRootPath)
    {
        var warnings = new List<string>();
        var normalizedRuns = new List<NormalizedRun>();
        var skippedResults = 0;

        if (sarif is null)
        {
            warnings.Add("SARIF envelope is null.");
            return new NormalizationResult([], 0, warnings);
        }

        var rootPath = string.IsNullOrWhiteSpace(repoRootPath) ? "." : repoRootPath;

        try
        {
            var root = sarif.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("SARIF envelope root must be an object.");
                return new NormalizationResult([], 0, warnings);
            }

            if (!TryGetString(root, "version", out var version) || !string.Equals(version, "2.1.0", StringComparison.Ordinal))
            {
                warnings.Add($"SARIF version must be '2.1.0' but was '{version ?? "<missing>"}'.");
                return new NormalizationResult([], 0, warnings);
            }

            if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
            {
                warnings.Add("SARIF envelope must contain a non-empty runs array.");
                return new NormalizationResult([], 0, warnings);
            }

            var runIndex = 0;
            foreach (var run in runs.EnumerateArray())
            {
                if (!TryGetDriverName(run, out var driverName))
                {
                    warnings.Add($"Run {runIndex} is missing tool.driver.name and was skipped.");
                    runIndex++;
                    continue;
                }

                var source = _sourceIdentifier.Resolve(driverName);
                var rules = _ruleCollector.Collect(run);
                var originalUriBaseIds = ExtractOriginalUriBaseIds(run);
                var runResults = new List<NormalizedResult>();

                if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    var resultIndex = 0;
                    foreach (var result in results.EnumerateArray())
                    {
                        try
                        {
                            var normalized = NormalizeResult(
                                result,
                                runIndex,
                                resultIndex,
                                rootPath,
                                originalUriBaseIds,
                                rules,
                                warnings);

                            if (normalized is null)
                            {
                                skippedResults++;
                            }
                            else
                            {
                                runResults.Add(normalized);
                            }
                        }
                        catch (Exception ex)
                        {
                            skippedResults++;
                            warnings.Add($"Run {runIndex} result {resultIndex} was skipped: {ex.Message}");
                        }

                        resultIndex++;
                    }
                }

                normalizedRuns.Add(new NormalizedRun(source, runResults));
                runIndex++;
            }

            if (normalizedRuns.Count == 0)
                warnings.Add("SARIF envelope did not contain any runs with tool.driver.name.");

            return new NormalizationResult(normalizedRuns, skippedResults, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to normalize SARIF: {ex.Message}");
            return new NormalizationResult([], 0, warnings);
        }
    }

    private NormalizedResult? NormalizeResult(
        JsonElement result,
        int runIndex,
        int resultIndex,
        string repoRootPath,
        IReadOnlyDictionary<string, string> originalUriBaseIds,
        IReadOnlyDictionary<string, RuleDescriptor> rules,
        ICollection<string> warnings)
    {
        if (!TryGetString(result, "ruleId", out var ruleId))
        {
            warnings.Add($"Run {runIndex} result {resultIndex} skipped: missing ruleId.");
            return null;
        }

        rules.TryGetValue(ruleId, out var rule);

        if (!TryGetPrimaryLocation(result, out var artifactLocation, out var region))
        {
            warnings.Add($"Run {runIndex} result {resultIndex} skipped: missing location with artifactLocation.uri.");
            return null;
        }

        if (!TryGetString(artifactLocation, "uri", out var rawUri))
        {
            warnings.Add($"Run {runIndex} result {resultIndex} skipped: artifactLocation.uri was empty.");
            return null;
        }

        string? uriBaseId = null;
        if (TryGetString(artifactLocation, "uriBaseId", out var parsedUriBaseId))
            uriBaseId = parsedUriBaseId;

        var message = ResolveMessage(result, rule);
        if (string.IsNullOrWhiteSpace(message))
        {
            warnings.Add($"Run {runIndex} result {resultIndex} skipped: missing message text.");
            return null;
        }

        var normalizedPath = _pathNormalizer.Normalize(
            rawUri,
            uriBaseId,
            originalUriBaseIds,
            repoRootPath,
            warnings);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            warnings.Add($"Run {runIndex} result {resultIndex} skipped: normalized path was empty.");
            return null;
        }

        var level = _severityResolver.ResolveLevel(result, rule);
        var normalizedRegion = NormalizeRegion(region);

        var partialFingerprints = ReadStringDictionary(result, "partialFingerprints");
        var fingerprints = ReadStringDictionary(result, "fingerprints");

        var data = BuildDataPayload(result, rule);

        return new NormalizedResult(
            RuleId: ruleId,
            Message: message,
            Level: level,
            NormalizedPath: normalizedPath,
            Region: normalizedRegion,
            PartialFingerprints: partialFingerprints,
            Fingerprints: fingerprints,
            RuleMetadata: CloneJsonObject(rule?.Metadata),
            Data: data);
    }

    private JsonObject? BuildDataPayload(JsonElement result, RuleDescriptor? rule)
    {
        var payload = new JsonObject();

        if (result.TryGetProperty("codeFlows", out var codeFlows) && codeFlows.ValueKind != JsonValueKind.Null)
            payload["codeFlows"] = ToNode(codeFlows);

        if (result.TryGetProperty("relatedLocations", out var relatedLocations) && relatedLocations.ValueKind != JsonValueKind.Null)
            payload["relatedLocations"] = ToNode(relatedLocations);

        if (result.TryGetProperty("fixes", out var fixes) && fixes.ValueKind != JsonValueKind.Null)
            payload["fixes"] = ToNode(fixes);

        if (result.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            payload["properties"] = ToNode(properties);

        if (TryGetString(result, "level", out var originalLevel))
            payload["originalLevel"] = originalLevel;

        var toolSeverity = _severityResolver.ExtractToolSpecificSeverity(result, rule);
        if (toolSeverity is not null && toolSeverity.Count > 0)
            payload["toolSeverity"] = toolSeverity;

        return payload.Count == 0 ? null : payload;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringDictionary(JsonElement result, string propertyName)
    {
        if (!result.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Object)
            return null;

        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            if (TryElementToString(property.Value, out var text))
                dictionary[property.Name] = text;
        }

        return dictionary.Count == 0 ? null : dictionary;
    }

    private static NormalizedRegion? NormalizeRegion(JsonElement region)
    {
        if (region.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetInt32(region, "startLine", out var startLineValue) || !startLineValue.HasValue)
            return null;

        var startLine = startLineValue.Value;
        TryGetInt32(region, "startColumn", out var startColumn);
        TryGetInt32(region, "endLine", out var endLine);
        TryGetInt32(region, "endColumn", out var endColumn);

        return new NormalizedRegion(startLine, startColumn, endLine, endColumn);
    }

    private static string? ResolveMessage(JsonElement result, RuleDescriptor? rule)
    {
        if (!result.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetString(message, "text", out var text))
            return text;

        if (TryGetString(message, "markdown", out var markdown))
            return markdown;

        if (TryGetString(message, "id", out var messageId)
            && rule is not null
            && rule.MessageStrings.TryGetValue(messageId, out var resolved))
        {
            return resolved;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ExtractOriginalUriBaseIds(JsonElement run)
    {
        if (!run.TryGetProperty("originalUriBaseIds", out var originalUriBaseIds)
            || originalUriBaseIds.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseId in originalUriBaseIds.EnumerateObject())
        {
            if (baseId.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetString(baseId.Value, "uri", out var uri))
                continue;

            values[baseId.Name] = uri;
        }

        return values;
    }

    private static bool TryGetPrimaryLocation(
        JsonElement result,
        out JsonElement artifactLocation,
        out JsonElement region)
    {
        artifactLocation = default;
        region = default;

        if (!result.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var location in locations.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object)
                continue;

            if (!location.TryGetProperty("physicalLocation", out var physicalLocation)
                || physicalLocation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!physicalLocation.TryGetProperty("artifactLocation", out artifactLocation)
                || artifactLocation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetString(artifactLocation, "uri", out _))
                continue;

            if (!physicalLocation.TryGetProperty("region", out region))
                region = default;

            return true;
        }

        return false;
    }

    private static bool TryGetDriverName(JsonElement run, out string driverName)
    {
        driverName = string.Empty;
        return run.TryGetProperty("tool", out var tool)
               && tool.ValueKind == JsonValueKind.Object
               && tool.TryGetProperty("driver", out var driver)
               && driver.ValueKind == JsonValueKind.Object
               && TryGetString(driver, "name", out driverName);
    }

    private static bool TryGetString(JsonElement owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        var str = property.GetString();
        if (string.IsNullOrWhiteSpace(str))
            return false;

        value = str;
        return true;
    }

    private static bool TryGetInt32(JsonElement owner, string propertyName, out int? value)
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
        {
            value = intValue;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryElementToString(JsonElement value, out string text)
    {
        text = string.Empty;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                text = value.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private static JsonNode? ToNode(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText());
    }

    private static JsonObject? CloneJsonObject(JsonObject? value)
    {
        return value?.DeepClone() as JsonObject;
    }
}
