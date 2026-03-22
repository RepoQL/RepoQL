using System.Text;
using AwesomeAssertions;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Cloud;

internal sealed class CloudAuthSessionStoreTests
{
    [Test]
    public async Task SaveAsync_PersistsAccessIdAndRefreshTokens()
    {
        using var tempDir = new TempDir();
        var refreshStore = new FakeRefreshTokenStore();
        var store = CreateStore(tempDir.Path, refreshStore);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        await store.SaveAsync(new CloudAuthSession("access-token", expiresAt, "id-token", "refresh-token"));
        var loaded = await store.ReadAsync();

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("access-token");
        loaded.IdToken.Should().Be("id-token");
        loaded.ExpiresAt.Should().Be(expiresAt);
        refreshStore.StoredToken.Should().Be("refresh-token");
    }

    [Test]
    public async Task ClearAsync_RemovesPersistedSessionAndRefreshToken()
    {
        using var tempDir = new TempDir();
        var refreshStore = new FakeRefreshTokenStore();
        var store = CreateStore(tempDir.Path, refreshStore);

        await store.SaveAsync(new CloudAuthSession("access-token", DateTimeOffset.UtcNow.AddMinutes(30), "id-token", "refresh-token"));
        await store.ClearAsync();

        (await store.ReadAsync()).Should().BeNull();
        (await store.GetRefreshTokenAsync()).Should().BeNull();
        File.Exists(Path.Combine(tempDir.Path, "auth.json")).Should().BeFalse();
    }

    [Test]
    public void TryReadClaims_ReadsCommonJwtClaims()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = CreateJwt(expiresAt, "user_123", "person@example.com");

        JwtPayloadReader.TryReadClaims(token, out var claims).Should().BeTrue();
        claims.Should().NotBeNull();
        claims!.Subject.Should().Be("user_123");
        claims.Email.Should().Be("person@example.com");
        claims.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds()));
    }

    private static CloudAuthSessionStore CreateStore(string userConfigDir, IRefreshTokenStore refreshTokenStore)
    {
        var resolved = ConfigurationLoader.Load(SettingRegistry.Build(), repoRoot: null, userConfigDir: userConfigDir);
        return new CloudAuthSessionStore(
            resolved,
            refreshTokenStore: refreshTokenStore,
            authFilePath: Path.Combine(userConfigDir, "auth.json"));
    }

    private static string CreateJwt(DateTimeOffset expiresAt, string subject, string email)
    {
        static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Encode($$"""{"sub":"{{subject}}","email":"{{email}}","exp":{{expiresAt.ToUnixTimeSeconds()}}}""");
        return $"{header}.{payload}.";
    }

    private sealed class FakeRefreshTokenStore : IRefreshTokenStore
    {
        public string? StoredToken { get; private set; }

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
                "repoql-auth-store-tests-" + Guid.NewGuid().ToString("N"));
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
