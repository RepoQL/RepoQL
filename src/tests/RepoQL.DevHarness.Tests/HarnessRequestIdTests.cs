using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessRequestIdTests
{
    [Test]
    public async Task Create_UsesExpectedFormat()
    {
        var id = HarnessRequestId.Create();

        id.Should().MatchRegex("^req_\\d{14}_[0-9a-f]{4}$");
    }
}
