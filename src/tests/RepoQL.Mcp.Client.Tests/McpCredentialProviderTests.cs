using System.Text.Json;
using AwesomeAssertions;

namespace RepoQL.Mcp.Client.Tests;

public class McpCredentialProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _claudeCredPath;
    private readonly string _repoqlCredPath;

    public McpCredentialProviderTests()
    {
        // Create isolated temp directories for each test
        _tempDir = Path.Combine(Path.GetTempPath(), $"mcp_cred_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var claudeDir = Path.Combine(_tempDir, ".claude");
        var repoqlDir = Path.Combine(_tempDir, ".repoql");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(repoqlDir);

        _claudeCredPath = Path.Combine(claudeDir, ".credentials.json");
        _repoqlCredPath = Path.Combine(repoqlDir, ".mcp-credentials.json");
    }

    public void Dispose()
    {
        // Clean up temp directories
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    #region GetCredentialsAsync - Claude Store

    [Test]
    public async Task GetCredentialsAsync_FindsCredentialsInClaudeStore()
    {
        // Arrange
        var serverUrl = "https://mcp.example.com/mcp/";
        var credentials = new
        {
            mcpOAuth = new Dictionary<string, object>
            {
                ["example|abc123"] = new
                {
                    serverUrl = serverUrl,
                    clientId = "client-123",
                    accessToken = "test-access-token",
                    refreshToken = "test-refresh-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                    scope = "openid profile"
                }
            }
        };
        await File.WriteAllTextAsync(_claudeCredPath, JsonSerializer.Serialize(credentials));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetCredentialsAsync(serverUrl);

        // Assert
        result.Should().NotBeNull();
        result!.ServerUrl.Should().Be(serverUrl);
        result.AccessToken.Should().Be("test-access-token");
        result.RefreshToken.Should().Be("test-refresh-token");
        result.ClientId.Should().Be("client-123");
        result.Scope.Should().Be("openid profile");
        result.IsExpired.Should().BeFalse();
    }

    [Test]
    public async Task GetCredentialsAsync_MatchesByNormalizedUrl()
    {
        // Arrange - stored URL has trailing slash, query URL doesn't
        var storedUrl = "https://mcp.example.com/mcp/";
        var queryUrl = "https://mcp.example.com/mcp";
        var credentials = new
        {
            mcpOAuth = new Dictionary<string, object>
            {
                ["example|abc"] = new
                {
                    serverUrl = storedUrl,
                    accessToken = "token123",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_claudeCredPath, JsonSerializer.Serialize(credentials));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetCredentialsAsync(queryUrl);

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("token123");
    }

    [Test]
    public async Task GetCredentialsAsync_IsCaseInsensitive()
    {
        // Arrange
        var credentials = new
        {
            mcpOAuth = new Dictionary<string, object>
            {
                ["example|abc"] = new
                {
                    serverUrl = "https://MCP.Example.COM/MCP/",
                    accessToken = "token-case",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_claudeCredPath, JsonSerializer.Serialize(credentials));

        var provider = CreateProvider();

        // Act - query with different case
        var result = await provider.GetCredentialsAsync("https://mcp.example.com/mcp/");

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("token-case");
    }

    #endregion

    #region GetCredentialsAsync - RepoQL Store

    [Test]
    public async Task GetCredentialsAsync_FallsBackToRepoqlStore()
    {
        // Arrange - no Claude credentials, only RepoQL
        var serverUrl = "https://mcp.example.com/";
        var credentials = new
        {
            credentials = new[]
            {
                new
                {
                    serverUrl = serverUrl,
                    accessToken = "repoql-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_repoqlCredPath, JsonSerializer.Serialize(credentials));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetCredentialsAsync(serverUrl);

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("repoql-token");
    }

    [Test]
    public async Task GetCredentialsAsync_PrefersClaudeOverRepoql()
    {
        // Arrange - both stores have credentials for same server
        var serverUrl = "https://mcp.example.com/";

        var claudeCreds = new
        {
            mcpOAuth = new Dictionary<string, object>
            {
                ["example|abc"] = new
                {
                    serverUrl = serverUrl,
                    accessToken = "claude-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_claudeCredPath, JsonSerializer.Serialize(claudeCreds));

        var repoqlCreds = new
        {
            credentials = new[]
            {
                new
                {
                    serverUrl = serverUrl,
                    accessToken = "repoql-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_repoqlCredPath, JsonSerializer.Serialize(repoqlCreds));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetCredentialsAsync(serverUrl);

        // Assert - Claude should win
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("claude-token");
    }

    #endregion

    #region GetCredentialsAsync - Not Found

    [Test]
    public async Task GetCredentialsAsync_ReturnsNullWhenNotFound()
    {
        // Arrange - empty stores
        var provider = CreateProvider();

        // Act
        var result = await provider.GetCredentialsAsync("https://unknown.example.com/");

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetCredentialsAsync_ReturnsNullForDifferentServer()
    {
        // Arrange
        var credentials = new
        {
            mcpOAuth = new Dictionary<string, object>
            {
                ["example|abc"] = new
                {
                    serverUrl = "https://mcp.example.com/",
                    accessToken = "token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_claudeCredPath, JsonSerializer.Serialize(credentials));

        var provider = CreateProvider();

        // Act - query for different server
        var result = await provider.GetCredentialsAsync("https://other.example.com/");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region SaveCredentialsAsync

    [Test]
    public async Task SaveCredentialsAsync_CreatesNewFile()
    {
        // Arrange
        var provider = CreateProvider();
        var credentials = new McpCredentials
        {
            ServerUrl = "https://new.example.com/",
            AccessToken = "new-token",
            RefreshToken = "new-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Scope = "email"
        };

        // Act
        await provider.SaveCredentialsAsync(credentials);

        // Assert - should be readable from RepoQL store
        var result = await provider.GetCredentialsAsync("https://new.example.com/");
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("new-token");
    }

    [Test]
    public async Task SaveCredentialsAsync_UpdatesExistingCredentials()
    {
        // Arrange
        var serverUrl = "https://update.example.com/";
        var initialCreds = new
        {
            credentials = new[]
            {
                new
                {
                    serverUrl = serverUrl,
                    accessToken = "old-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_repoqlCredPath, JsonSerializer.Serialize(initialCreds));

        var provider = CreateProvider();
        var newCredentials = new McpCredentials
        {
            ServerUrl = serverUrl,
            AccessToken = "updated-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        };

        // Act
        await provider.SaveCredentialsAsync(newCredentials);

        // Assert
        var result = await provider.GetCredentialsAsync(serverUrl);
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("updated-token");
    }

    [Test]
    public async Task SaveCredentialsAsync_PreservesOtherCredentials()
    {
        // Arrange - existing credentials for different server
        var existingCreds = new
        {
            credentials = new[]
            {
                new
                {
                    serverUrl = "https://existing.example.com/",
                    accessToken = "existing-token",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
                }
            }
        };
        await File.WriteAllTextAsync(_repoqlCredPath, JsonSerializer.Serialize(existingCreds));

        var provider = CreateProvider();
        var newCredentials = new McpCredentials
        {
            ServerUrl = "https://new.example.com/",
            AccessToken = "new-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        // Act
        await provider.SaveCredentialsAsync(newCredentials);

        // Assert - both should exist
        var existingResult = await provider.GetCredentialsAsync("https://existing.example.com/");
        var newResult = await provider.GetCredentialsAsync("https://new.example.com/");

        existingResult.Should().NotBeNull();
        existingResult!.AccessToken.Should().Be("existing-token");

        newResult.Should().NotBeNull();
        newResult!.AccessToken.Should().Be("new-token");
    }

    #endregion

    #region McpCredentials

    [Test]
    public async Task IsExpired_ReturnsFalseForFutureExpiry()
    {
        var credentials = new McpCredentials
        {
            ServerUrl = "https://example.com/",
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        credentials.IsExpired.Should().BeFalse();
    }

    [Test]
    public async Task IsExpired_ReturnsTrueForPastExpiry()
    {
        var credentials = new McpCredentials
        {
            ServerUrl = "https://example.com/",
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        credentials.IsExpired.Should().BeTrue();
    }

    [Test]
    public async Task IsExpired_ReturnsTrueWithin30SecondBuffer()
    {
        var credentials = new McpCredentials
        {
            ServerUrl = "https://example.com/",
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20) // Within 30-second buffer
        };

        credentials.IsExpired.Should().BeTrue();
    }

    [Test]
    public async Task CanRefresh_ReturnsTrueWithRefreshToken()
    {
        var credentials = new McpCredentials
        {
            ServerUrl = "https://example.com/",
            AccessToken = "token",
            RefreshToken = "refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        credentials.CanRefresh.Should().BeTrue();
    }

    [Test]
    public async Task CanRefresh_ReturnsFalseWithoutRefreshToken()
    {
        var credentials = new McpCredentials
        {
            ServerUrl = "https://example.com/",
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        credentials.CanRefresh.Should().BeFalse();
    }

    #endregion

    private McpCredentialProvider CreateProvider()
    {
        return new McpCredentialProvider(_claudeCredPath, _repoqlCredPath);
    }
}
