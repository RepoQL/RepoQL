using RepoQL.Contracts;

namespace RepoQL.Formats.Sql;

/// <summary>
/// Parsed state for SQL files.
/// </summary>
internal sealed class SqlState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    /// <summary>All SQL objects found in the file.</summary>
    public required IReadOnlyList<SqlObject> Objects { get; init; }
}

/// <summary>
/// A SQL object (table, view, function, index, etc.)
/// </summary>
internal sealed class SqlObject
{
    public required SqlObjectType Type { get; init; }
    public required string Name { get; init; }
    public int Line { get; init; }

    /// <summary>For tables: columns. For functions: parameters.</summary>
    public IReadOnlyList<SqlColumn> Columns { get; init; } = [];

    /// <summary>For functions: return type.</summary>
    public string? ReturnType { get; init; }

    /// <summary>For indexes: the table name.</summary>
    public string? OnTable { get; init; }

    /// <summary>For indexes: column list.</summary>
    public IReadOnlyList<string> IndexColumns { get; init; } = [];

    /// <summary>For views: tables referenced.</summary>
    public IReadOnlyList<string> SourceTables { get; init; } = [];

    /// <summary>Whether this is a UNIQUE index/constraint.</summary>
    public bool IsUnique { get; init; }
}

internal enum SqlObjectType
{
    Table,
    View,
    Function,
    Procedure,
    Index,
    Trigger,
    Macro // DuckDB
}

internal sealed class SqlColumn
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public string? Default { get; init; }
}
