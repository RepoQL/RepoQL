using AwesomeAssertions;
using RepoQL.Mcp.Client.Configuration;

namespace RepoQL.Mcp.Client.Tests.Configuration;

public class DirectoryMcpConfigSourceTests
{
    private string _tempDir = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repoql-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [After(Test)]
    public async Task TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region LoadConfigs

    [Test]
    public async Task LoadConfigs_WithNoConfigFiles_ReturnsEmpty()
    {
        var source = new DirectoryMcpConfigSource(_tempDir);

        var result = source.LoadConfigs();

        result.Should().BeEmpty();
    }

    [Test]
    public async Task LoadConfigs_WithMcpJson_LoadsConfig()
    {
        var configPath = Path.Combine(_tempDir, ".mcp.json");
        await File.WriteAllTextAsync(configPath, """
            {
                "mcpServers": {
                    "test": { "command": "test-command" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var result = source.LoadConfigs();

        result.Should().ContainKey("test");
        result["test"].Command.Should().Be("test-command");
    }

    [Test]
    public async Task LoadConfigs_WithRepoqlMcpJson_LoadsConfig()
    {
        var configPath = Path.Combine(_tempDir, ".repoql.mcp.json");
        await File.WriteAllTextAsync(configPath, """
            {
                "mcpServers": {
                    "repoql-specific": { "command": "repoql-cmd" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var result = source.LoadConfigs();

        result.Should().ContainKey("repoql-specific");
    }

    [Test]
    public async Task LoadConfigs_WithNestedRepoqlMcpJson_LoadsConfig()
    {
        var repoqlDir = Path.Combine(_tempDir, ".repoql");
        Directory.CreateDirectory(repoqlDir);
        var configPath = Path.Combine(repoqlDir, ".mcp.json");
        await File.WriteAllTextAsync(configPath, """
            {
                "mcpServers": {
                    "nested": { "command": "nested-cmd" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var result = source.LoadConfigs();

        result.Should().ContainKey("nested");
    }

    [Test]
    public async Task LoadConfigs_LaterFilesOverrideEarlier()
    {
        // Create .mcp.json with initial config
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".mcp.json"), """
            {
                "mcpServers": {
                    "shared": { "command": "original" },
                    "only-in-first": { "command": "first" }
                }
            }
            """);

        // Create .repoql.mcp.json with override
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".repoql.mcp.json"), """
            {
                "mcpServers": {
                    "shared": { "command": "overridden" },
                    "only-in-second": { "command": "second" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var result = source.LoadConfigs();

        // Shared server should be overridden
        result["shared"].Command.Should().Be("overridden");
        // Both unique servers should be present
        result.Should().ContainKey("only-in-first");
        result.Should().ContainKey("only-in-second");
    }

    [Test]
    public async Task LoadConfigs_HandlesInvalidJson_Gracefully()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".mcp.json"), "{ invalid json }");

        var source = new DirectoryMcpConfigSource(_tempDir);
        var result = source.LoadConfigs();

        // Should not throw, just return empty
        result.Should().BeEmpty();
    }

    #endregion

    #region Properties

    [Test]
    public async Task Name_ContainsDirectory()
    {
        var source = new DirectoryMcpConfigSource(_tempDir);

        source.Name.Should().Contain(_tempDir);
    }

    [Test]
    public async Task Priority_IsStandardPriority()
    {
        var source = new DirectoryMcpConfigSource(_tempDir);

        source.Priority.Should().Be(DirectoryMcpConfigSource.StandardPriority);
    }

    #endregion
}
