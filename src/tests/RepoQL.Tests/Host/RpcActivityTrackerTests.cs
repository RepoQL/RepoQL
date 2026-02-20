using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

internal sealed class RpcActivityTrackerTests
{
    [Test]
    public void Given_LongRunningRequest_When_CapturingSnapshot_Then_HangingRequestIsReported()
    {
        var threshold = TimeSpan.FromSeconds(5);
        var tracker = new RpcActivityTracker(hangThreshold: threshold);
        var now = new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc);

        using var _ = tracker.BeginScope(
            "/repoql.v1.RepoQL/ExecuteRawQuery",
            startedAtUtc: now.Subtract(TimeSpan.FromSeconds(12)));

        var snapshot = tracker.CaptureSnapshot(now);
        snapshot.ActiveCount.Should().Be(1);
        snapshot.HangingCount.Should().Be(1);
        snapshot.HangThresholdMs.Should().Be(5_000);
        snapshot.OldestRequestAgeMs.Should().BeGreaterThanOrEqualTo(12_000);
        snapshot.OldestRequestMethod.Should().Be("/repoql.v1.RepoQL/ExecuteRawQuery");
    }

    [Test]
    public void Given_ExcludedInfrastructureMethod_When_BeginScope_Then_RequestIsIgnored()
    {
        var threshold = TimeSpan.FromSeconds(1);
        var tracker = new RpcActivityTracker(hangThreshold: threshold);
        var now = new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc);

        using var _ = tracker.BeginScope(
            "/repoql.v1.RepoQL/WatchStatus",
            startedAtUtc: now.Subtract(TimeSpan.FromMinutes(2)));
        using var __ = tracker.BeginScope(
            "/grpc.health.v1.Health/Watch",
            startedAtUtc: now.Subtract(TimeSpan.FromMinutes(2)));

        var snapshot = tracker.CaptureSnapshot(now);
        snapshot.ActiveCount.Should().Be(0);
        snapshot.HangingCount.Should().Be(0);
        snapshot.OldestRequestMethod.Should().BeNull();
        snapshot.OldestRequestAgeMs.Should().BeNull();
    }
}
