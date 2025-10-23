namespace RepoQL.Contracts;

/// <summary>
///     Represents an optional schema snippet contributed by a format loader.
/// </summary>
/// <param name="Identifier">A descriptive identifier for logging.</param>
/// <param name="Sql">The SQL to execute.</param>
public readonly record struct FormatSqlScript(string Identifier, string Sql);
