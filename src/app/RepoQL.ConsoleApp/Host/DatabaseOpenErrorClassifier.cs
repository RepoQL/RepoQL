using DuckDB.NET.Data;
using RepoQL.Data.DuckDB;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Classify database open failures for recovery and diagnostics.
/// Complexity: Pattern-matches common DuckDB error strings into known categories.
/// </summary>
internal static class DatabaseOpenErrorClassifier
{
    public static DatabaseOpenErrorType Classify(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            var classified = ClassifySingle(current);
            if (classified != DatabaseOpenErrorType.Other)
                return classified;

            current = current.InnerException;
        }

        return DatabaseOpenErrorType.Other;
    }

    private static DatabaseOpenErrorType ClassifySingle(Exception ex)
    {
        if (ex is DuckDbSchemaMismatchException)
            return DatabaseOpenErrorType.SchemaMismatch;

        if (ex is UnauthorizedAccessException)
            return DatabaseOpenErrorType.Permission;

        if (ex is IOException io && IsSharingViolation(io))
            return DatabaseOpenErrorType.Locked;

        var message = ex.Message ?? string.Empty;

        if (message.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("locked", StringComparison.OrdinalIgnoreCase))
            return DatabaseOpenErrorType.Locked;

        if (message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid file", StringComparison.OrdinalIgnoreCase))
            return DatabaseOpenErrorType.Corrupted;

        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            return DatabaseOpenErrorType.Permission;

        if (message.Contains("no space left", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            return DatabaseOpenErrorType.DiskFull;

        if (ex is DuckDBException duck && IsDuckDbLockError(duck))
            return DatabaseOpenErrorType.Locked;

        return DatabaseOpenErrorType.Other;
    }

    private static bool IsDuckDbLockError(DuckDBException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("exclusive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSharingViolation(IOException ex)
    {
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        return ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation;
    }
}

internal enum DatabaseOpenErrorType
{
    Locked,
    Corrupted,
    Permission,
    DiskFull,
    SchemaMismatch,
    Other
}
