namespace RepoQL.Contracts;

/// <summary>
/// Provides SQL schema scripts for format-specific views and functions.
/// </summary>
public interface IFormatSchemaProvider
{
    /// <summary>
    /// Gets the SQL scripts that create format-specific views over the core schema.
    /// </summary>
    IEnumerable<FormatSqlScript> GetSchemaScripts();
}
