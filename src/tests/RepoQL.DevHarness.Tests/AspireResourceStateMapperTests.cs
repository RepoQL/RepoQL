using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class AspireResourceStateMapperTests
{
    [Test]
    [Arguments("Running", HostState.Ready)]
    [Arguments("Stopped", HostState.Stopped)]
    [Arguments("Exited", HostState.Stopped)]
    [Arguments("Starting", HostState.Unknown)]
    public async Task MapToHostState_MapsAspireStates(string resourceState, string expected)
    {
        var result = AspireResourceStateMapper.MapToHostState(resourceState);

        result.Should().Be(expected);
    }

    [Test]
    public async Task MapToHostState_UnknownWhenMissing()
    {
        var result = AspireResourceStateMapper.MapToHostState(null);

        result.Should().Be(HostState.Unknown);
    }
}
