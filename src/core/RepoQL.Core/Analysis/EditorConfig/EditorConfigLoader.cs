using System.Collections.Concurrent;

namespace RepoQL.Core.Analysis.EditorConfig;

internal sealed class EditorConfigLoader
{
    private readonly ConcurrentDictionary<string, EditorConfigDocument> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EditorConfigDocument Load(string path)
    {
        return _cache.GetOrAdd(path, ParseEditorConfig);
    }

    private static EditorConfigDocument ParseEditorConfig(string path) 
    {
        if (!File.Exists(path))
        {
            return new EditorConfigDocument();
        }

        var lines = File.ReadAllLines(path);
        var entries = new List<EditorConfigEntry>();
        var currentPatterns = new List<string>();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isRoot = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";"))
            {
                continue;
            }

            if (line.StartsWith("["))
            {
                if (currentPatterns.Count > 0 || properties.Count > 0)
                {
                    entries.Add(new EditorConfigEntry
                    {
                        Patterns = currentPatterns.ToArray(),
                        Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase)
                    });
                }

                properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var section = line.Trim('[', ']');
                currentPatterns = section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(pattern => pattern.Replace('\\', '/')).ToList();
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx < 0)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            if (string.Equals(key, "root", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out var rootValue))
                {
                    isRoot = rootValue;
                }
                continue;
            }

            properties[key] = value;
        }

        if (currentPatterns.Count > 0 || properties.Count > 0)
        {
            entries.Add(new EditorConfigEntry
            {
                Patterns = currentPatterns.ToArray(),
                Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase)
            });
        }

        return new EditorConfigDocument
        {
            IsRoot = isRoot,
            Entries = entries
        };
    }
}
