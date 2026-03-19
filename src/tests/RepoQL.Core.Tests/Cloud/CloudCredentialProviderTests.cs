using System.Net;
using System.Net.Mime;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts.Cloud;
using RepoQL.Core.Cloud;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Cloud;

internal sealed class CloudCredentialProviderTests
{
    [Test]
    public async Task GetTokenAsync_WithoutStoredTokens_ThrowsLoginMessage()
    {
        using var tempDir = new TempDir();
        var provider = CreateProvider(tempDir.Path, refreshTokenStore: new FakeRefreshTokenStore());

        var exception = await Assert.That(() => provider.GetTokenAsync()).Throws<InvalidOperationException>();

        exception!.Message.Should().Be(CloudCredentialProvider.NotAuthenticatedMessage);
    }

    [Test]
    public async Task GetTokenAsync_WithHealthyCachedToken_ReturnsWithoutRefresh()
    {
        using var tempDir = new TempDir();
        var token = CreateJwt(DateTimeOffset.UtcNow.AddMinutes(10));
        await WriteAuthFileAsync(tempDir.Path, token, DateTimeOffset.UtcNow.AddMinutes(10));

        using var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException("refresh should not run"));
        using var provider = CreateProvider(
            tempDir.Path,
            httpClient: new HttpClient(handler),
            refreshTokenStore: new FakeRefreshTokenStore("refresh-token"));

        var value = await provider.GetTokenAsync();

        value.Should().Be(token);
        handler.CallCount.Should().Be(0);
    }

    [Test]
    public async Task GetTokenAsync_RefreshesExpiredToken_AndRotatesRefreshToken()
    {
        using var tempDir = new TempDir();
        var expiredToken = CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-5));
        var refreshedToken = CreateJwt(DateTimeOffset.UtcNow.AddMinutes(20));
        await WriteAuthFileAsync(tempDir.Path, expiredToken, DateTimeOffset.UtcNow.AddMinutes(-5));

        string? capturedBody = null;
        using var handler = new RecordingHttpHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "access_token": "{{refreshedToken}}",
                      "refresh_token": "rt_new",
                      "id_token": "id_ignored",
                      "token_type": "bearer",
                      "expires_in": 3600
                    }
                    """,
                    Encoding.UTF8,
                    MediaTypeNames.Application.Json)
            };
        });

        var refreshStore = new FakeRefreshTokenStore("rt_old");
        using var provider = CreateProvider(
            tempDir.Path,
            httpClient: new HttpClient(handler),
            refreshTokenStore: refreshStore);

        var token = await provider.GetTokenAsync();

        token.Should().Be(refreshedToken);
        refreshStore.StoredToken.Should().Be("rt_new");
        capturedBody.Should().Contain("grant_type=refresh_token");
        capturedBody.Should().Contain($"client_id={RepoQlConfig.CloudSettings.DefaultClientId}");
        capturedBody.Should().NotContain("client_secret");
        capturedBody.Should().Contain("refresh_token=rt_old");

        var persisted = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "auth.json"));
        persisted.Should().Contain(refreshedToken);
    }

    [Test]
    public async Task GetTokenAsync_WhenRefreshTokenIsRevoked_ThrowsSessionExpired()
    {
        using var tempDir = new TempDir();
        await WriteAuthFileAsync(tempDir.Path, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-2)), DateTimeOffset.UtcNow.AddMinutes(-2));

        var handler = new RecordingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, MediaTypeNames.Application.Json)
        }));

        var provider = CreateProvider(
            tempDir.Path,
            httpClient: new HttpClient(handler),
            refreshTokenStore: new FakeRefreshTokenStore("rt_old"));

        var exception = await Assert.That(() => provider.GetTokenAsync()).Throws<InvalidOperationException>();

        exception!.Message.Should().Be(CloudCredentialProvider.SessionExpiredMessage);
    }

    [Test]
    public async Task GetTokenAsync_RetriesNetworkFailure_Once()
    {
        using var tempDir = new TempDir();
        await WriteAuthFileAsync(tempDir.Path, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-2)), DateTimeOffset.UtcNow.AddMinutes(-2));

        var attempts = 0;
        var handler = new RecordingHttpHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("offline");
        });

        var provider = CreateProvider(
            tempDir.Path,
            httpClient: new HttpClient(handler),
            refreshTokenStore: new FakeRefreshTokenStore("rt_old"));

        var exception = await Assert.That(() => provider.GetTokenAsync()).Throws<InvalidOperationException>();

        exception!.Message.Should().Be(CloudCredentialProvider.NetworkErrorMessage);
        attempts.Should().Be(2);
    }

    [Test]
    public async Task GetTokenAsync_WhenLockTimesOut_ReReadsSharedToken()
    {
        using var tempDir = new TempDir();
        await WriteAuthFileAsync(tempDir.Path, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-2)), DateTimeOffset.UtcNow.AddMinutes(-2));

        await using var heldLock = new FileStream(
            Path.Combine(tempDir.Path, ".auth-lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var refreshedToken = CreateJwt(DateTimeOffset.UtcNow.AddMinutes(5));
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await WriteAuthFileAsync(tempDir.Path, refreshedToken, DateTimeOffset.UtcNow.AddMinutes(5));
        });

        var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException("network refresh should be skipped"));
        var provider = CreateProvider(
            tempDir.Path,
            httpClient: new HttpClient(handler),
            refreshTokenStore: new FakeRefreshTokenStore("rt_old"),
            lockTimeout: TimeSpan.FromMilliseconds(150));

        var token = await provider.GetTokenAsync();

        token.Should().Be(refreshedToken);
        handler.CallCount.Should().Be(0);
    }

    [Test]
    public void AddCloudCredentialProvider_RegistersDynamicProviderWithoutStoredCredentials()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path, envReader: _ => null);

        var services = new ServiceCollection();
        services.AddSingleton(resolved);
        services.AddLogging();
        services.AddCloudCredentialProvider();

        using var provider = services.BuildServiceProvider();
        var credentialProvider = provider.GetRequiredService<ICloudCredentialProvider?>();

        credentialProvider.Should().BeOfType<CloudCredentialProvider>();
    }

    [Test]
    public void AddCloudCredentialProvider_UsesStaticProviderWhenApiKeyIsConfigured()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path, envReader: _ => null);
        resolved.Settings.Cloud.ApiKey = "rql_api-key";

        var services = new ServiceCollection();
        services.AddSingleton(resolved);
        services.AddLogging();
        services.AddCloudCredentialProvider();

        using var provider = services.BuildServiceProvider();
        var credentialProvider = provider.GetRequiredService<ICloudCredentialProvider?>();

        credentialProvider.Should().BeOfType<CloudCredentialProvider>();
    }

    [Test]
    public async Task GetTokenAsync_FallsBackToApiKeyWhenNoOAuthCredentials()
    {
        using var tempDir = new TempDir();
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: tempDir.Path);
        resolved.Settings.Cloud.ApiKey = "rql_test-key";

        var sessionStore = new CloudAuthSessionStore(resolved, refreshTokenStore: new FakeRefreshTokenStore());
        using var provider = new CloudCredentialProvider(resolved, sessionStore, logger: null);

        var token = await provider.GetTokenAsync();

        token.Should().Be("rql_test-key");
    }

    private static CloudCredentialProvider CreateProvider(
        string userConfigDir,
        HttpClient? httpClient = null,
        IRefreshTokenStore? refreshTokenStore = null,
        TimeSpan? lockTimeout = null)
    {
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: userConfigDir, envReader: _ => null);
        var sessionStore = new CloudAuthSessionStore(
            resolved,
            refreshTokenStore: refreshTokenStore);
        return new CloudCredentialProvider(
            resolved,
            sessionStore,
            httpClient: httpClient,
            lockTimeout: lockTimeout);
    }

    private static async Task WriteAuthFileAsync(string userConfigDir, string accessToken, DateTimeOffset expiresAt)
    {
        Directory.CreateDirectory(userConfigDir);
        await File.WriteAllTextAsync(
            Path.Combine(userConfigDir, "auth.json"),
            $$"""
            {"accessToken":"{{accessToken}}","expiresAt":"{{expiresAt:O}}"}
            """);
    }

    private static string CreateJwt(DateTimeOffset expiresAt)
    {
        static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Encode($$"""{"sub":"user_123","exp":{{expiresAt.ToUnixTimeSeconds()}}}""");
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

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return await handler(request);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "repoql-cloud-tests-" + Guid.NewGuid().ToString("N"));
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
