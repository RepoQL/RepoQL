using AwesomeAssertions;
using Grpc.Core;
using RepoQL.Client.Diagnostics;
using RepoQL.Protocol;

namespace RepoQL.Cli.Tests.Diagnostics;

/// <summary>
/// Purpose: Verify user errors do not trigger infrastructure diagnostics.
/// Complexity: Exercises classifier rules with in-memory exceptions only.
/// </summary>
internal sealed class ErrorClassifierTests
{
    [Test]
    public void InvalidArgument_IsNotInfrastructureError()
    {
        var rpc = new RpcException(new Status(StatusCode.InvalidArgument, "Parser Error: bad sql"));
        ErrorClassifier.IsInfrastructureError(rpc).Should().BeFalse();
    }

    [Test]
    public void DiagnosticsException_IsInfrastructureError()
    {
        var diagnostics = new RepoQlDiagnostics(
            RepoRoot: "repo",
            SocketPath: "/tmp/repoql.sock",
            ChannelState: "TransientFailure",
            HealthStatus: "WatchFaulted",
            HealthWatchFaulted: true,
            Host: null,
            RecoveryAttempted: true,
            CircuitBreakerOpen: false,
            CircuitBreakerFailures: 2,
            CircuitBreakerWindow: TimeSpan.FromMinutes(5));
        var timeout = new TimeoutException("boom");
        Action act = () => diagnostics.Throw("failed", timeout);
        var ex = act.Should().Throw<RepoQlDiagnosticsException>().Which;

        ErrorClassifier.IsInfrastructureError(ex).Should().BeTrue();
        ErrorClassifier.GetCleanMessage(ex).Should().Be("boom");
    }

    [Test]
    public void EnrichSqlError_BinderError_AppendDescribeHint()
    {
        var message = """
            Binder Error: Referenced column "author" not found in FROM clause!
            Candidate bindings: "git_recent.author_name", "git_recent.author_email", "git_recent.message"
            """;

        var enriched = ErrorClassifier.EnrichSqlError(message);

        enriched.Should().Contain("DESCRIBE git_recent");
        enriched.Should().Contain("Tip:");
        enriched.Should().Contain("help:///schema/views/git-recent.md");
    }

    [Test]
    public void EnrichSqlError_BinderError_MultipleTablesInJoin()
    {
        var message = """
            Binder Error: Referenced column "content" not found in FROM clause!
            Candidate bindings: "files.uri", "files.lang", "types.name", "types.namespace"
            """;

        var enriched = ErrorClassifier.EnrichSqlError(message);

        enriched.Should().Contain("DESCRIBE files");
        enriched.Should().Contain("DESCRIBE types");
    }

    [Test]
    public void EnrichSqlError_CatalogError_AppendShowTablesHint()
    {
        var message = """
            Catalog Error: Table with name "git_log" does not exist!
            """;

        var enriched = ErrorClassifier.EnrichSqlError(message);

        enriched.Should().Contain("SHOW TABLES");
        enriched.Should().Contain("help:///schema/**");
    }

    [Test]
    public void EnrichSqlError_NonSqlError_PassesThrough()
    {
        var message = "Some other error that is not SQL related";

        ErrorClassifier.EnrichSqlError(message).Should().Be(message);
    }

    [Test]
    public void ExtractTableNames_ParsesCandidateBindings()
    {
        var message = """Candidate bindings: "files.uri", "files.source", "types.name".""";

        var tables = ErrorClassifier.ExtractTableNames(message);

        tables.Should().Contain("files");
        tables.Should().Contain("types");
    }

    [Test]
    public void ExtractTableNames_DeduplicatesSameTable()
    {
        var message = """Candidate bindings: "files.uri", "files.source", "files.lang".""";

        var tables = ErrorClassifier.ExtractTableNames(message);

        tables.Where(t => t.Equals("files", StringComparison.OrdinalIgnoreCase)).Should().HaveCount(1);
    }

    [Test]
    public void ExtractTableNames_FromClauseFallback()
    {
        // DuckDB sometimes returns bare column names without table prefix
        var message = """
            Binder Error: Referenced column "author" not found in FROM clause!
            Candidate bindings: "author_date", "author_name", "author_email"
            LINE 1: SELECT author FROM git_recent LIMIT 5
                           ^
            """;

        var tables = ErrorClassifier.ExtractTableNames(message);

        tables.Should().Contain("git_recent");
    }

    [Test]
    public void EnrichSqlError_BinderError_FallsBackToFromClause()
    {
        var message = """
            Binder Error: Referenced column "author" not found in FROM clause!
            Candidate bindings: "author_date", "author_name", "author_email"
            LINE 1: SELECT author FROM git_recent LIMIT 5
                           ^
            """;

        var enriched = ErrorClassifier.EnrichSqlError(message);

        enriched.Should().Contain("DESCRIBE git_recent");
        enriched.Should().Contain("help:///schema/views/git-recent.md");
    }

    [Test]
    public void EnrichSqlError_BinderError_CoreTablePointsToCoreDoc()
    {
        var message = """
            Binder Error: Referenced column "content" not found in FROM clause!
            Candidate bindings: "text_content", "headline"
            LINE 1: SELECT content FROM artifact LIMIT 1
                           ^
            """;

        var enriched = ErrorClassifier.EnrichSqlError(message);

        enriched.Should().Contain("DESCRIBE artifact");
        enriched.Should().Contain("help:///schema/core.md");
    }
}
