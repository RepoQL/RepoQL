using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using RepoQL.ConsoleApp.Auth;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;

namespace RepoQL.Cli.Tests;

internal sealed class CloudAuthServiceTests
{
    [Test]
    public void GenerateCodeVerifier_UsesPkceSafeCharacters()
    {
        var verifier = CloudAuthService.GenerateCodeVerifier();

        verifier.Length.Should().BeGreaterThanOrEqualTo(43);
        verifier.Length.Should().BeLessThanOrEqualTo(128);
        verifier.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Test]
    public void GenerateCodeChallenge_MatchesRfc7636Example()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        var challenge = CloudAuthService.GenerateCodeChallenge(verifier);

        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Test]
    public void ComputeHashPrefix_ReturnsFirstEightHexCharacters()
    {
        CloudAuthService.ComputeHashPrefix("secret").Should().Be("2BB80D53");
    }

    [Test]
    public void GetEndpoints_UsesConfiguredCloudSettings()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        resolved.Settings.Cloud.ClientId = "client_test";
        resolved.Settings.Cloud.AuthorizationEndpoint = "https://auth.example.test/custom/authorize";
        resolved.Settings.Cloud.DeviceAuthorizationEndpoint = "https://auth.example.test/custom/device";
        resolved.Settings.Cloud.AuthenticateEndpoint = "https://auth.example.test/custom/authenticate";
        using var service = CreateService(resolved);

        service.GetAuthorizationEndpoint().ToString().Should().Be("https://auth.example.test/custom/authorize");
        service.GetDeviceAuthorizationEndpoint().ToString().Should().Be("https://auth.example.test/custom/device");
        service.GetAuthenticateEndpoint().ToString().Should().Be("https://auth.example.test/custom/authenticate");
    }

    [Test]
    public void CreateDeviceCodePollForm_DoesNotIncludeClientSecret()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        using var service = CreateService(resolved);

        var form = service.CreateDeviceCodePollForm("client_test", "device_123");

        form.Should().NotContainKey("client_secret");
        form["client_id"].Should().Be("client_test");
        form["device_code"].Should().Be("device_123");
    }

    private static CloudAuthService CreateService(ResolvedConfig resolved)
    {
        var sessionStore = new CloudAuthSessionStore(resolved);
        return new CloudAuthService(resolved, sessionStore, logger: (ILogger<CloudAuthService>?)null);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "repoql-cli-cloud-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
