using AwesomeAssertions;
using RepoQL.Contracts.Cloud;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Cloud;

internal sealed class CloudAuthStatusProviderTests
{
    [Test]
    public async Task GetStatusAsync_WithApiKey_ReturnsPaidApiKeyStatus()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        resolved.Settings.Cloud.ApiKey = "rql_test-key";

        var provider = new CloudAuthStatusProvider(
            resolved,
            new CloudAuthSessionStore(resolved, refreshTokenStore: new FakeRefreshTokenStore()));

        var status = await provider.GetStatusAsync();

        status.IsAuthenticated.Should().BeTrue();
        status.IsPayingCustomer.Should().BeTrue();
        status.AccessMethod.Should().Be(CloudAccessMethod.ApiKey);
    }

    [Test]
    public async Task GetStatusAsync_WithSessionOrganizationId_ReturnsPaidSessionStatus()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        var store = new CloudAuthSessionStore(resolved, refreshTokenStore: new FakeRefreshTokenStore("refresh-token"));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await store.SaveAsync(new CloudAuthSession(
            CreateJwt(expiresAt, email: "paid@example.com", organizationId: "org_paid"),
            expiresAt,
            CreateJwt(expiresAt, email: "paid@example.com", organizationId: "org_paid")));

        var provider = new CloudAuthStatusProvider(resolved, store);

        var status = await provider.GetStatusAsync();

        status.IsAuthenticated.Should().BeTrue();
        status.IsPayingCustomer.Should().BeTrue();
        status.AccessMethod.Should().Be(CloudAccessMethod.Session);
        status.Email.Should().Be("paid@example.com");
        status.OrganizationId.Should().Be("org_paid");
    }

    [Test]
    public async Task GetStatusAsync_WithSessionWithoutOrganizationId_ReturnsAuthenticatedButNotPaid()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        var store = new CloudAuthSessionStore(resolved, refreshTokenStore: new FakeRefreshTokenStore("refresh-token"));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await store.SaveAsync(new CloudAuthSession(
            CreateJwt(expiresAt, email: "free@example.com"),
            expiresAt,
            CreateJwt(expiresAt, email: "free@example.com")));

        var provider = new CloudAuthStatusProvider(resolved, store);

        var status = await provider.GetStatusAsync();

        status.IsAuthenticated.Should().BeTrue();
        status.IsPayingCustomer.Should().BeFalse();
        status.AccessMethod.Should().Be(CloudAccessMethod.Session);
        status.Email.Should().Be("free@example.com");
        status.OrganizationId.Should().BeNull();
    }

    private static string CreateJwt(DateTimeOffset expiresAt, string? email = null, string? organizationId = null)
    {
        static string Encode(string json)
            => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var properties = new List<string>
        {
            "\"sub\":\"user_123\"",
            $"\"exp\":{expiresAt.ToUnixTimeSeconds()}"
        };

        if (!string.IsNullOrWhiteSpace(email))
            properties.Add($"\"email\":\"{email}\"");
        if (!string.IsNullOrWhiteSpace(organizationId))
            properties.Add($"\"org_id\":\"{organizationId}\"");

        var payload = Encode("{" + string.Join(",", properties) + "}");
        return $"{header}.{payload}.";
    }

    private sealed class FakeRefreshTokenStore(string? token = null) : IRefreshTokenStore
    {
        public string? StoredToken { get; private set; } = token;

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(StoredToken);

        public Task SetAsync(string refreshToken, CancellationToken cancellationToken)
        {
            StoredToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            StoredToken = null;
            return Task.CompletedTask;
        }

        public Task<bool> HasAnyAsync(CancellationToken cancellationToken)
            => Task.FromResult(!string.IsNullOrWhiteSpace(StoredToken));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "repoql-cloud-auth-tests-" + Guid.NewGuid().ToString("N"));
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
