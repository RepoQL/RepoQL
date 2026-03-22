using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis.EditorConfig;

public sealed class EditorConfigSettingsProvider(string repoRoot) : IAnalyzerSettingsProvider
{
    private readonly EditorConfigLoader _loader = new();
    private readonly string _repoRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(repoRoot) ? Directory.GetCurrentDirectory() : repoRoot);

    public AnalyzerSettings Resolve(string containerUri, SemanticMediaType media, Node documentNode)
    {
        var path = ResolveRepoRelativePath(containerUri);
        if (path is null)
            return new AnalyzerSettings();

        var merged = new Dictionary<string, AnalyzerRuleSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in EnumerateEntries(path))
        {
            foreach (var kv in entry.Properties)
            {
                if (!kv.Key.StartsWith("repoql.analyzer.", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = kv.Key.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    continue;

                var ruleId = parts[2];
                var property = parts[3];

                if (!merged.TryGetValue(ruleId, out var current))
                {
                    current = new AnalyzerRuleSettings { RuleId = ruleId };
                }

                if (string.Equals(property, "severity", StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        Severity = ParseSeverity(kv.Value)
                    };
                }
                else if (string.Equals(property, "autofix", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(kv.Value, out var b))
                    {
                        current = current with { EnableAutoFix = b };
                    }
                }
                else
                {
                    var props = new Dictionary<string, string>(current.Properties, StringComparer.OrdinalIgnoreCase)
                    {
                        [property] = kv.Value
                    };
                    current = current with { Properties = props };
                }

                merged[ruleId] = current;
            }
        }

        return new AnalyzerSettings(merged);
    }

    private IEnumerable<EditorConfigEntry> EnumerateEntries(string repoRelativePath)
    {
        var absolutePath = Path.Combine(_repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var currentDirectory = Path.GetDirectoryName(absolutePath) ?? _repoRoot;
        var repoRootFull = Path.GetFullPath(_repoRoot);

        var documents = new List<(EditorConfigDocument Document, string Directory)>();

        while (currentDirectory.StartsWith(repoRootFull, StringComparison.OrdinalIgnoreCase))
        {
            var configPath = Path.Combine(currentDirectory, ".editorconfig");
            if (File.Exists(configPath))
            {
                var document = _loader.Load(configPath);
                documents.Add((document, currentDirectory));
                if (document.IsRoot)
                    break;
            }

            if (string.Equals(currentDirectory, repoRootFull, StringComparison.OrdinalIgnoreCase))
                break;

            var parent = Directory.GetParent(currentDirectory);
            if (parent is null)
                break;
            currentDirectory = parent.FullName;
        }

        documents.Reverse();

        foreach (var (document, directory) in documents)
        {
            var relativeToDirectory = Path.GetRelativePath(directory, absolutePath).Replace('\\', '/');
            if (relativeToDirectory == string.Empty)
                relativeToDirectory = Path.GetFileName(absolutePath);

            foreach (var entry in document.Entries)
            {
                if (entry.Patterns.Count == 0 || EditorConfigMatcher.Matches(relativeToDirectory, entry.Patterns))
                    yield return entry;
            }
        }
    }

    private static AnalysisSeverity ParseSeverity(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return AnalysisSeverity.Warning;

        return value?.ToLowerInvariant() switch
        {
            "error" => AnalysisSeverity.Error,
            "warning" => AnalysisSeverity.Warning,
            "suggestion" or "info" or "information" => AnalysisSeverity.Suggestion,
            "none" or "silent" => AnalysisSeverity.None,
            _ => AnalysisSeverity.Warning
        };
    }

    private static string? ResolveRepoRelativePath(string uri)
    {
        try
        {
            var parsed = RepoUri.Parse(uri);
            return !string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase) ? null : parsed.AbsolutePath.TrimStart('/');
        }
        catch
        {
            return null;
        }
    }
}
