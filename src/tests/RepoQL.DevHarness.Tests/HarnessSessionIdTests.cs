using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessSessionIdTests
{
    [Test]
    public async Task Create_UsesExpectedFormat()
    {
        var id = HarnessSessionId.Create();

        id.Should().MatchRegex("^sess_\\d{14}_[0-9a-f]{4}$");
    }
}
