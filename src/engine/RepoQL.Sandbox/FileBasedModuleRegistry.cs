using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Persist module registrations in .repoql/modules/ and serve source to the sandbox.
/// Complexity: Manifest-backed file registry with path validation, hash verification, and in-process locking.
/// </summary>
public sealed class FileBasedModuleRegistry(string repoRoot) : IModuleRegistry
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _modulesRoot = Path.Combine(Path.GetFullPath(repoRoot ?? throw new ArgumentNullException(nameof(repoRoot))), ".repoql", "modules");
    private readonly string _manifestPath = Path.Combine(Path.GetFullPath(repoRoot ?? throw new ArgumentNullException(nameof(repoRoot))), ".repoql", "modules", "manifest.json");

    public ModuleRegistrationResult Register(string identifier)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!TryNormalizeIdentifier(identifier, out var normalizedIdentifier, out var validationError))
        {
            errors.Add(validationError);
            return new ModuleRegistrationResult(false, errors, warnings);
        }

        var sourceRelativePath = BuildModuleRelativePath("src", normalizedIdentifier, ".mjs");
        var docsRelativePath = BuildModuleRelativePath("docs", normalizedIdentifier, ".md");
        var sourceFullPath = ResolveModulePath(sourceRelativePath);
        var docsFullPath = ResolveModulePath(docsRelativePath);

        if (!File.Exists(sourceFullPath))
        {
            errors.Add($"Source file not found: {sourceRelativePath}");
            return new ModuleRegistrationResult(false, errors, warnings);
        }

        string source;
        try
        {
            source = File.ReadAllText(sourceFullPath);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to read {sourceRelativePath}: {ex.Message}");
            return new ModuleRegistrationResult(false, errors, warnings);
        }

        var docsPath = File.Exists(docsFullPath) ? docsRelativePath : null;
        if (docsPath is null)
            warnings.Add($"Docs file not found: {docsRelativePath}");

        var registration = new RegisteredModule(
            Identifier: normalizedIdentifier,
            Specifier: ToSpecifier(normalizedIdentifier),
            SourcePath: sourceRelativePath,
            DocsPath: docsPath,
            SourceHash: ComputeSourceHash(source),
            Capabilities: InferCapabilities(source),
            RegisteredAt: DateTimeOffset.UtcNow,
            IsHealthy: true);

        lock (_gate)
        {
            Directory.CreateDirectory(_modulesRoot);

            var manifest = LoadManifestEntries();
            var existingIndex = manifest.FindIndex(entry => string.Equals(entry.Identifier, normalizedIdentifier, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                manifest[existingIndex] = registration;
            else
                manifest.Add(registration);

            SaveManifestEntries(manifest);
        }

        return new ModuleRegistrationResult(true, errors, warnings);
    }

    public bool Remove(string identifier)
    {
        if (!TryNormalizeIdentifier(identifier, out var normalizedIdentifier, out _))
            return false;

        lock (_gate)
        {
            var manifest = LoadManifestEntries();
            var removed = manifest.RemoveAll(entry => string.Equals(entry.Identifier, normalizedIdentifier, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveManifestEntries(manifest);
            return removed;
        }
    }

    public IReadOnlyList<RegisteredModule> List()
    {
        lock (_gate)
        {
            var manifest = LoadManifestEntries();
            return manifest
                .Select(entry => entry with { IsHealthy = ComputeHealth(entry).IsHealthy })
                .ToList();
        }
    }

    public string? LoadSource(string specifier)
    {
        var identifier = ParseSpecifier(specifier);
        if (identifier is null)
            return null;

        lock (_gate)
        {
            var entry = LoadManifestEntries()
                .FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;

            var sourcePath = ResolveModulePath(entry.SourcePath);
            if (!File.Exists(sourcePath))
                return null;

            try
            {
                return File.ReadAllText(sourcePath);
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<ModuleHealthResult> CheckHealth()
    {
        lock (_gate)
        {
            return LoadManifestEntries()
                .Select(ComputeHealth)
                .ToList();
        }
    }

    private ModuleHealthResult ComputeHealth(RegisteredModule entry)
    {
        try
        {
            var sourcePath = ResolveModulePath(entry.SourcePath);
            if (!File.Exists(sourcePath))
                return new ModuleHealthResult(entry.Identifier, false, $"Missing source file: {entry.SourcePath}");

            var source = File.ReadAllText(sourcePath);
            var actualHash = ComputeSourceHash(source);
            if (!string.Equals(actualHash, entry.SourceHash, StringComparison.Ordinal))
            {
                return new ModuleHealthResult(
                    entry.Identifier,
                    false,
                    $"Source hash mismatch: expected {entry.SourceHash}, got {actualHash}");
            }

            return new ModuleHealthResult(entry.Identifier, true, null);
        }
        catch (Exception ex)
        {
            return new ModuleHealthResult(entry.Identifier, false, ex.Message);
        }
    }

    private List<RegisteredModule> LoadManifestEntries()
    {
        if (!File.Exists(_manifestPath))
            return [];

        var text = File.ReadAllText(_manifestPath);
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var node = JsonNode.Parse(text);
        if (node is not JsonArray array)
            return [];

        var registrations = new List<RegisteredModule>();
        foreach (var item in array)
        {
            if (item is JsonObject obj && TryParseRegisteredModule(obj, out var module))
                registrations.Add(module);
        }

        return registrations;
    }

    private void SaveManifestEntries(IReadOnlyList<RegisteredModule> manifest)
    {
        Directory.CreateDirectory(_modulesRoot);

        var json = new JsonArray();
        foreach (var entry in manifest.OrderBy(module => module.Identifier, StringComparer.OrdinalIgnoreCase))
            json.Add((JsonNode)ToJson(entry));

        var tempPath = _manifestPath + ".tmp";
        File.WriteAllText(tempPath, json.ToJsonString(ManifestJsonOptions), new UTF8Encoding(false));
        try
        {
            File.Move(tempPath, _manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static JsonObject ToJson(RegisteredModule entry)
    {
        var capabilities = new JsonObject
        {
            ["reads"] = entry.Capabilities.Reads,
            ["writes"] = entry.Capabilities.Writes,
            ["deletes"] = entry.Capabilities.Deletes
        };

        var json = new JsonObject
        {
            ["identifier"] = entry.Identifier,
            ["specifier"] = entry.Specifier,
            ["sourcePath"] = entry.SourcePath,
            ["sourceHash"] = entry.SourceHash,
            ["capabilities"] = capabilities,
            ["registeredAt"] = entry.RegisteredAt.ToString("O")
        };

        if (!string.IsNullOrWhiteSpace(entry.DocsPath))
            json["docsPath"] = entry.DocsPath;

        return json;
    }

    private static bool TryParseRegisteredModule(JsonObject json, out RegisteredModule module)
    {
        module = default!;

        var identifier = json["identifier"]?.GetValue<string>();
        var specifier = json["specifier"]?.GetValue<string>();
        var sourcePath = json["sourcePath"]?.GetValue<string>();
        var docsPath = json["docsPath"]?.GetValue<string>();
        var sourceHash = json["sourceHash"]?.GetValue<string>();
        var registeredAtRaw = json["registeredAt"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(identifier) ||
            string.IsNullOrWhiteSpace(specifier) ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(sourceHash) ||
            string.IsNullOrWhiteSpace(registeredAtRaw) ||
            !DateTimeOffset.TryParse(registeredAtRaw, out var registeredAt))
        {
            return false;
        }

        var capabilitiesJson = json["capabilities"] as JsonObject;
        var capabilities = new DeclaredCapabilities(
            Reads: capabilitiesJson?["reads"]?.GetValue<bool>() ?? false,
            Writes: capabilitiesJson?["writes"]?.GetValue<bool>() ?? false,
            Deletes: capabilitiesJson?["deletes"]?.GetValue<bool>() ?? false);

        module = new RegisteredModule(
            identifier,
            specifier,
            sourcePath,
            docsPath,
            sourceHash,
            capabilities,
            registeredAt,
            IsHealthy: true);
        return true;
    }

    private string ResolveModulePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_modulesRoot, relativePath));
        var modulesRootWithSeparator = _modulesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _modulesRoot
            : _modulesRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(modulesRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _modulesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved path escapes modules root: {relativePath}");
        }

        return fullPath;
    }

    private static bool TryNormalizeIdentifier(string identifier, out string normalizedIdentifier, out string error)
    {
        normalizedIdentifier = string.Empty;
        error = "Identifier is required.";

        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var normalized = identifier.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized[0] != '@')
        {
            error = $"Invalid identifier '{identifier}'. Expected @prefix/name.";
            return false;
        }

        if (normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Contains("/./", StringComparison.Ordinal) ||
            normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.EndsWith("/.", StringComparison.Ordinal) ||
            normalized.EndsWith("/..", StringComparison.Ordinal))
        {
            error = $"Invalid identifier '{identifier}'. Path traversal is not allowed.";
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            error = $"Invalid identifier '{identifier}'. Expected @prefix/name.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                error = $"Invalid identifier '{identifier}'. Path traversal is not allowed.";
                return false;
            }

            foreach (var ch in segment)
            {
                if (char.IsLetterOrDigit(ch) || ch is '@' or '-' or '_' or '.')
                    continue;

                error = $"Invalid identifier '{identifier}'. Unsupported character '{ch}'.";
                return false;
            }
        }

        normalizedIdentifier = normalized;
        error = string.Empty;
        return true;
    }

    private static string? ParseSpecifier(string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier))
            return null;

        var trimmed = specifier.Trim();
        if (trimmed.StartsWith("repoql:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["repoql:".Length..];

        return TryNormalizeIdentifier(trimmed, out var identifier, out _) ? identifier : null;
    }

    private static string BuildModuleRelativePath(string folder, string identifier, string extension)
        => Path.Combine(folder, identifier.Replace('/', Path.DirectorySeparatorChar) + extension)
            .Replace('\\', '/');

    private static string ToSpecifier(string identifier) => $"repoql:{identifier}";

    private static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToUpperInvariant()}";
    }

    private static DeclaredCapabilities InferCapabilities(string source) => new(
        Reads: source.Contains("repoql.read", StringComparison.Ordinal),
        Writes: source.Contains("repoql.write", StringComparison.Ordinal),
        Deletes: source.Contains("repoql.delete", StringComparison.Ordinal));
}
