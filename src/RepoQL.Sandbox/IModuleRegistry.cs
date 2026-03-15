namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Manage agent-authored module registration, validation, and discovery.
/// Complexity: File-based manifest under .repoql/modules/. Validation at registration.
/// Concurrent access serialized through in-process lock.
/// </summary>
public interface IModuleRegistry
{
    ModuleRegistrationResult Register(string identifier);
    bool Remove(string identifier);
    IReadOnlyList<RegisteredModule> List();
    string? LoadSource(string specifier);
    IReadOnlyList<ModuleHealthResult> CheckHealth();
}

public sealed record RegisteredModule(
    string Identifier,
    string Specifier,
    string SourcePath,
    string? DocsPath,
    string SourceHash,
    DeclaredCapabilities Capabilities,
    DateTimeOffset RegisteredAt,
    bool IsHealthy);

public sealed record DeclaredCapabilities(bool Reads, bool Writes, bool Deletes)
{
    public static readonly DeclaredCapabilities None = new(false, false, false);
    public static readonly DeclaredCapabilities ReadOnly = new(true, false, false);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Reads) parts.Add("read");
        if (Writes) parts.Add("write");
        if (Deletes) parts.Add("delete");
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }
}

public sealed record ModuleRegistrationResult(
    bool Success,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record ModuleHealthResult(
    string Identifier,
    bool IsHealthy,
    string? Problem);
