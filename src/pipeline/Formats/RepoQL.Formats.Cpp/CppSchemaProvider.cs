using RepoQL.Contracts;

namespace RepoQL.Formats.Cpp;

/// <summary>
/// C/C++ schema provider for format-specific SQL views.
///
/// Purpose: Register C/C++ SQL views from embedded schema resources.
///
/// Complexity: Embedded-resource script loading with optional runtime enablement.
/// </summary>
public sealed class CppSchemaProvider(bool enableViews = false) : IFormatSchemaProvider
{
    private readonly bool _enableViews = enableViews;
    private static readonly Lazy<string> CppViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Cpp.Schema.cpp_views.sql"));

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        if (!_enableViews)
        {
            yield break;
        }

        yield return new FormatSqlScript("cpp_views", CppViewsSql.Value);
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(CppSchemaProvider).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
