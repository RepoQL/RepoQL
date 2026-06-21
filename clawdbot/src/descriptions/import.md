<WHY>
RepoQL becomes more powerful the more context it contains, import allows you to pull in annotations and other data sources (usually repositories) into the graph and query them alongside the primary repository.

Import multiplies the usefulness of all of repoql's other tools
</WHY>

<SOURCES>
- GitHub: `github://owner/repo` or `github://owner/repo@ref`
- Git: `git://github.com/owner/repo.git` (any HTTPS git URL with the scheme replaced by `git://`)
- Local repo: `local:///absolute/path/to/dir`
- SARIF report: `sarif:///absolute/path/to/file.sarif`
</SOURCES>

<WHEN_TO_USE>
When you need to pull more context into the graph:
- When you are asked a question that requires more information than you have locally
- Comparing or tracing flows/history across systems in different repositories
- Building a knowledge graph of the other repositories that make up the product/company
- Pulling inspiration from related OSS projects
- Getting the full source of the packages you use to solve tricky issues or aid integration efforts
- Pulling in sarif formatted linting reports (roslyn/snyk/sonarqube/quodana/etc)
</WHEN_TO_USE>

<GUIDANCE>
To import, pass the URI directly:
- `import("github://anthropics/claude-code")`
- `import("github://owner/repo@v1.2.0")` — pin to a ref
- `import("sarif:///tmp/snyk-findings.sarif")`

To remove, prefix the URI with `-` — this deletes the import and all of its indexed data:
- `import("-github://owner/repo")`
- `import("-local:///abs/path/to/dir")`

Importing something already imported does an incremental update for versioned filesystems like git repositories, and a replace for things like sarif

VFS imports (`github://`, `git://`, `local://`) return immediately with an operation ID. The `Operations` view tracks progress in flight:
```sql
SELECT scope, state, ready_percent, runtime_s
FROM Operations WHERE kind = 'import' AND NOT is_terminal;
```
`ready_percent` reaches 100 when every file in the scope has been indexed.

Annotation imports (`sarif://`) complete synchronously and return a summary message.

To see all non-annotation imports: `SELECT * FROM Filesystems`
Full documentation: `help://tools/import.md`
</GUIDANCE>
