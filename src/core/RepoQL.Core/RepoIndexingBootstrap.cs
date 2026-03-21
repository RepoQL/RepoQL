

using RepoQL.Contracts;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Core;

/// <summary>
/// Helper to build a repository store, registry, and filter for a given filesystem root and DB filename.
/// </summary>
public static class RepoIndexingBootstrap
{
    /// <summary>Create the repo store and registry for the given filesystem root and DB filename.</summary>
    public static (PhysicalFileSystem store, FileSystemRegistry registry, RepoGitIgnoreFilter filter) Create(string repoRootPath, string dbFileName)
    {
        var store = new PhysicalFileSystem(repoRootPath);
        var registry = new FileSystemRegistry([store]);
        var filter = new RepoGitIgnoreFilter(repoRootPath, [dbFileName]);
        return (store, registry, filter);
    }

    /// <summary>Build a DuckDB connection string for a DB file at the repo root.</summary>
    public static string DuckDbConnectionString(string repoRootPath, string dbFileName)
    {
        var full = Path.Combine(Path.GetFullPath(repoRootPath), dbFileName);
        // DuckDB ADO uses "Data Source=" semantics.
        return $"Data Source={full}";
    }

    /// <summary>Create a canonical repo root URI (<c>file:///</c>).</summary>
    public static RepoUri RepoRootUri() => RepoUri.Create(new Uri("file:///"));
}