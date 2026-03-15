using RepoQL.Contracts;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Validate URIs against configured scopes before sandbox capability execution.
/// Complexity: Pattern matching against scope lists per operation type.
/// </summary>
public sealed class SandboxScopeEnforcer
{
    private readonly IReadOnlyList<string> _readScopes;
    private readonly IReadOnlyList<string> _writeScopes;
    private readonly IReadOnlyList<string> _deleteScopes;

    public SandboxScopeEnforcer(
        IReadOnlyList<string>? readScopes = null,
        IReadOnlyList<string>? writeScopes = null,
        IReadOnlyList<string>? deleteScopes = null)
    {
        _readScopes = readScopes ?? ["file://**", "help://**", "github://**"];
        _writeScopes = writeScopes ?? ["file://.repoql/tmp/**", "file:///.repoql/tmp/**"];
        _deleteScopes = deleteScopes ?? writeScopes ?? ["file://.repoql/tmp/**", "file:///.repoql/tmp/**"];
    }

    public void EnforceRead(string uri) => Enforce("Read", uri, _readScopes);

    public void EnforceWrite(string uri) => Enforce("Write", uri, _writeScopes);

    public void EnforceDelete(string uri) => Enforce("Delete", uri, _deleteScopes);

    private static void Enforce(string operation, string uri, IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new SandboxScopeException(operation, uri ?? string.Empty, scopes);

        foreach (var candidate in EnumerateCandidates(uri))
        {
            foreach (var scope in scopes)
            {
                if (UriPatternMatcher.Matches(candidate, scope) == true)
                    return;
            }
        }

        throw new SandboxScopeException(operation, uri, scopes);
    }

    private static IEnumerable<string> EnumerateCandidates(string uri)
    {
        yield return uri;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var path = parsed.AbsolutePath.Replace('\\', '/');

        if (!string.IsNullOrWhiteSpace(parsed.Host))
        {
            yield return $"file:///{parsed.Host.Trim('/')}{path}";
            yield break;
        }

        const string repoqlTmpPrefix = "/.repoql/";
        if (path.StartsWith(repoqlTmpPrefix, StringComparison.OrdinalIgnoreCase))
            yield return $"file://{path}";
    }
}

public sealed class SandboxScopeException : Exception
{
    public string Operation { get; }
    public string Uri { get; }
    public IReadOnlyList<string> AllowedScopes { get; }

    public SandboxScopeException()
        : this("Unknown", string.Empty, [])
    {
    }

    public SandboxScopeException(string message)
        : this(message, operation: "Unknown", uri: string.Empty, allowedScopes: [], innerException: null)
    {
    }

    public SandboxScopeException(string message, Exception innerException)
        : this(message, operation: "Unknown", uri: string.Empty, allowedScopes: [], innerException: innerException)
    {
    }

    public SandboxScopeException(string operation, string uri, IReadOnlyList<string> allowedScopes)
        : this(
            $"{operation} denied for '{uri}'. Allowed scopes: {string.Join(", ", allowedScopes)}. " +
            $"To change: ::config.set sandbox.{ToConfigKey(operation)}_scopes <pattern>",
            operation,
            uri,
            allowedScopes,
            null)
    {
    }

    private SandboxScopeException(
        string message,
        string operation,
        string uri,
        IReadOnlyList<string> allowedScopes,
        Exception? innerException)
        : base(message, innerException)
    {
        Operation = operation;
        Uri = uri;
        AllowedScopes = allowedScopes;
    }

    private static string ToConfigKey(string operation) => operation switch
    {
        "Read" => "read",
        "Write" => "write",
        "Delete" => "delete",
        _ => operation
    };
}
