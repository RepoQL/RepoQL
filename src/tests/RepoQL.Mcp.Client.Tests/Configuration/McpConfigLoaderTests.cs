using AwesomeAssertions;
using RepoQL.Mcp.Client.Configuration;

namespace RepoQL.Mcp.Client.Tests.Configuration;

public class McpConfigLoaderTests
{
    #region LoadFromJson

    [Test]
    public async Task LoadFromJson_WithMcpServersFormat_LoadsConfigs()
    {
        var json = """
            {
                "mcpServers": {
                    "test-server": {
                        "type": "stdio",
                        "command": "/usr/bin/test",
                        "args": ["--mode", "mcp"]
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result.Should().ContainKey("test-server");
        result["test-server"].Type.Should().Be("stdio");
        result["test-server"].Command.Should().Be("/usr/bin/test");
        result["test-server"].Args.Should().BeEquivalentTo(new[] { "--mode", "mcp" });
    }

    [Test]
    public async Task LoadFromJson_WithServersFormat_LoadsConfigs()
    {
        var json = """
            {
                "servers": {
                    "http-server": {
                        "type": "http",
                        "url": "http://localhost:8080/mcp"
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result.Should().ContainKey("http-server");
        result["http-server"].Type.Should().Be("http");
        result["http-server"].Url.Should().Be("http://localhost:8080/mcp");
    }

    [Test]
    public async Task LoadFromJson_DefaultsToStdioType()
    {
        var json = """
            {
                "mcpServers": {
                    "server": {
                        "command": "test"
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result["server"].Type.Should().Be("stdio");
    }

    [Test]
    public async Task LoadFromJson_ParsesEnvironmentVariables()
    {
        var json = """
            {
                "mcpServers": {
                    "server": {
                        "type": "stdio",
                        "command": "test",
                        "env": {
                            "API_KEY": "secret",
                            "DEBUG": "true"
                        }
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result["server"].Env.Should().ContainKey("API_KEY");
        result["server"].Env!["API_KEY"].Should().Be("secret");
        result["server"].Env.Should().ContainKey("DEBUG");
    }

    [Test]
    public async Task LoadFromJson_ParsesHeaders()
    {
        var json = """
            {
                "mcpServers": {
                    "server": {
                        "type": "http",
                        "url": "http://localhost:8080",
                        "headers": {
                            "Authorization": "Bearer ${TOKEN}",
                            "X-Custom": "value"
                        }
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result["server"].Headers.Should().ContainKey("Authorization");
        result["server"].Headers!["Authorization"].Should().Be("Bearer ${TOKEN}");
    }

    [Test]
    public async Task LoadFromJson_ParsesOAuthConfig()
    {
        var json = """
            {
                "mcpServers": {
                    "server": {
                        "type": "http",
                        "url": "http://localhost:8080",
                        "oauth": {
                            "redirectUri": "http://localhost:9000/callback",
                            "clientId": "my-client",
                            "clientSecret": "${SECRET}",
                            "scopes": ["read", "write"]
                        }
                    }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result["server"].OAuth.Should().NotBeNull();
        result["server"].OAuth!.RedirectUri.Should().Be("http://localhost:9000/callback");
        result["server"].OAuth.ClientId.Should().Be("my-client");
        result["server"].OAuth.Scopes.Should().BeEquivalentTo(new[] { "read", "write" });
    }

    [Test]
    public async Task LoadFromJson_WithEmptyJson_ReturnsEmpty()
    {
        var result = McpConfigLoader.LoadFromJson("{}");

        result.Should().BeEmpty();
    }

    [Test]
    public async Task LoadFromJson_WithNullOrWhitespace_ReturnsEmpty()
    {
        McpConfigLoader.LoadFromJson("").Should().BeEmpty();
        McpConfigLoader.LoadFromJson("  ").Should().BeEmpty();
    }

    [Test]
    public async Task LoadFromJson_WithMultipleServers_LoadsAll()
    {
        var json = """
            {
                "mcpServers": {
                    "server1": { "command": "cmd1" },
                    "server2": { "command": "cmd2" },
                    "server3": { "type": "http", "url": "http://localhost" }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        result.Should().HaveCount(3);
        result.Should().ContainKey("server1");
        result.Should().ContainKey("server2");
        result.Should().ContainKey("server3");
    }

    [Test]
    public async Task LoadFromJson_SupportsBothFormats_PrefersFirstFound()
    {
        var json = """
            {
                "mcpServers": {
                    "from-mcpServers": { "command": "cmd1" }
                },
                "servers": {
                    "from-servers": { "command": "cmd2" }
                }
            }
            """;

        var result = McpConfigLoader.LoadFromJson(json);

        // mcpServers is checked first, so only it should be loaded
        result.Should().ContainKey("from-mcpServers");
        result.Should().NotContainKey("from-servers");
    }

    #endregion
}
