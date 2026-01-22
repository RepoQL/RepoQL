using AwesomeAssertions;

namespace RepoQL.Protocol.Tests;

/// <summary>
/// Purpose: Verify in-process circuit breaker behavior for repeated connection failures.
/// Complexity: Exercises windowing and reset logic without external dependencies.
/// </summary>
internal sealed class ConnectionCircuitBreakerTests
{
    [Test]
    public void CircuitBreaker_OpensAfterThresholdAndResets()
    {
        var now = new DateTime(2026, 1, 22, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new ConnectionCircuitBreaker(3, TimeSpan.FromMinutes(5));

        breaker.IsOpen(now).Should().BeFalse();
        breaker.RecordFailure(now);
        breaker.IsOpen(now).Should().BeFalse();

        breaker.RecordFailure(now.AddMinutes(1));
        breaker.IsOpen(now.AddMinutes(1)).Should().BeFalse();

        breaker.RecordFailure(now.AddMinutes(2));
        breaker.IsOpen(now.AddMinutes(2)).Should().BeTrue();

        breaker.IsOpen(now.AddMinutes(7)).Should().BeFalse();
        breaker.FailureCount.Should().Be(0);
    }

    [Test]
    public void CircuitBreaker_RecordSuccessClearsFailures()
    {
        var now = new DateTime(2026, 1, 22, 13, 0, 0, DateTimeKind.Utc);
        var breaker = new ConnectionCircuitBreaker(3, TimeSpan.FromMinutes(5));

        breaker.RecordFailure(now);
        breaker.RecordFailure(now.AddMinutes(1));
        breaker.FailureCount.Should().Be(2);

        breaker.RecordSuccess(now.AddMinutes(2));
        breaker.FailureCount.Should().Be(0);
        breaker.IsOpen(now.AddMinutes(2)).Should().BeFalse();
    }
}
