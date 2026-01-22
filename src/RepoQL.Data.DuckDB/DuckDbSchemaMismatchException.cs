namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Signal schema compatibility failures that require database recreation.
/// Complexity: Simple exception type to differentiate schema mismatch from other errors.
/// </summary>
public sealed class DuckDbSchemaMismatchException : Exception
{
    public DuckDbSchemaMismatchException()
    {
    }

    public DuckDbSchemaMismatchException(string message)
        : base(message)
    {
    }

    public DuckDbSchemaMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
