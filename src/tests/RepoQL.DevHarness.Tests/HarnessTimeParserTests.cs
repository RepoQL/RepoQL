using System;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessTimeParserTests
{
    [Test]
    [Arguments("5m", "2026-02-05T14:25:00Z")]
    [Arguments("1h", "2026-02-05T13:30:00Z")]
    [Arguments("30s", "2026-02-05T14:29:30Z")]
    public async Task TryParse_ParsesRelativeDurations(string input, string expectedIso)
    {
        var now = new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero);

        var parsed = HarnessTimeParser.TryParse(input, now, out var result);

        parsed.Should().BeTrue();
        result.Should().Be(DateTimeOffset.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task TryParse_ParsesIsoTimestamp()
    {
        var now = new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero);

        var parsed = HarnessTimeParser.TryParse("2026-02-05T14:00:00Z", now, out var result);

        parsed.Should().BeTrue();
        result.Should().Be(new DateTimeOffset(2026, 2, 5, 14, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task TryParse_ReturnsFalseForInvalidInput()
    {
        var now = DateTimeOffset.UtcNow;

        var parsed = HarnessTimeParser.TryParse("not-a-time", now, out _);

        parsed.Should().BeFalse();
    }
}
