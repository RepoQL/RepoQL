using AwesomeAssertions;
using RepoQL.Client.Host;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

/// <summary>
/// Purpose: Guard database init recovery policy against destructive false positives.
/// Complexity: Verifies only schema mismatches are auto-rebuilt at startup.
/// </summary>
internal sealed class DatabaseInitCoordinatorTests
{
    [Test]
    public void ShouldAutoRebuildOnOpenFailure_ReturnsTrue_ForSchemaMismatch()
    {
        DatabaseInitCoordinator.ShouldAutoRebuildOnOpenFailure(DatabaseOpenErrorType.SchemaMismatch)
            .Should().BeTrue();
    }

    [Test]
    public void ShouldAutoRebuildOnOpenFailure_ReturnsFalse_ForCorruptionSignals()
    {
        DatabaseInitCoordinator.ShouldAutoRebuildOnOpenFailure(DatabaseOpenErrorType.Corrupted)
            .Should().BeFalse();
    }
}
