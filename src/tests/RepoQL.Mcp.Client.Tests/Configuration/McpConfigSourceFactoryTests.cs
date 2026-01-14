using AwesomeAssertions;
using RepoQL.Mcp.Client.Configuration;

namespace RepoQL.Mcp.Client.Tests.Configuration;

public class McpConfigSourceFactoryTests
{
    private string _tempDir = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repoql-factory-test-" + Guid.NewGuid().ToString("N")[..8]);
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

    #region CreateAll

    [Test]
    public async Task CreateAll_IncludesDirectorySource()
    {
        var sources = McpConfigSourceFactory.CreateAll(_tempDir);

        sources.Should().Contain(s => s is DirectoryMcpConfigSource);
    }

    [Test]
    public async Task CreateAll_WithNoEnabledAgents_OnlyIncludesDirectorySource()
    {
        var sources = McpConfigSourceFactory.CreateAll(_tempDir, enabledAgents: Array.Empty<AgentType>());

        sources.Should().HaveCount(1);
        sources.Single().Should().BeOfType<DirectoryMcpConfigSource>();
    }

    [Test]
    public async Task CreateAll_IncludesAgentSources_ByDefault()
    {
        var sources = McpConfigSourceFactory.CreateAll(_tempDir);

        // Should include directory source plus at least one agent source
        sources.Should().HaveCountGreaterThan(1);
        sources.Should().Contain(s => s is AgentMcpConfigSource);
    }

    [Test]
    public async Task CreateAll_SourcesAreOrderedByPriority()
    {
        var sources = McpConfigSourceFactory.CreateAll(_tempDir);

        // Sources should be ordered by priority (ascending)
        var priorities = sources.Select(s => s.Priority).ToList();
        priorities.Should().BeInAscendingOrder();
    }

    [Test]
    public async Task CreateAll_WithSpecificAgents_OnlyIncludesThoseAgents()
    {
        var sources = McpConfigSourceFactory.CreateAll(
            _tempDir,
            enabledAgents: new[] { AgentType.ClaudeCode });

        // Should have directory + ClaudeCode sources
        sources.Should().Contain(s => s is DirectoryMcpConfigSource);
        sources.Should().Contain(s => s is ClaudeCodeConfigSource);
        sources.Should().NotContain(s => s is ClaudeDesktopConfigSource);
    }

    #endregion

    #region CreateAgentSource

    [Test]
    public async Task CreateAgentSource_ClaudeCode_ReturnsCorrectType()
    {
        var source = McpConfigSourceFactory.CreateAgentSource(AgentType.ClaudeCode);

        source.Should().BeOfType<ClaudeCodeConfigSource>();
    }

    [Test]
    public async Task CreateAgentSource_ClaudeDesktop_ReturnsCorrectType()
    {
        var source = McpConfigSourceFactory.CreateAgentSource(AgentType.ClaudeDesktop);

        source.Should().BeOfType<ClaudeDesktopConfigSource>();
    }

    [Test]
    public async Task CreateAgentSource_UnsupportedAgent_ReturnsNull()
    {
        // Codex doesn't have an implementation yet
        var source = McpConfigSourceFactory.CreateAgentSource(AgentType.Codex);

        source.Should().BeNull();
    }

    #endregion

    #region CreateDirectorySource

    [Test]
    public async Task CreateDirectorySource_ReturnsDirectorySource()
    {
        var source = McpConfigSourceFactory.CreateDirectorySource(_tempDir);

        source.Should().BeOfType<DirectoryMcpConfigSource>();
    }

    #endregion

    #region LoadAndMerge

    [Test]
    public async Task LoadAndMerge_MergesMultipleSources()
    {
        // Create a test config file
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".mcp.json"), """
            {
                "mcpServers": {
                    "local": { "command": "local-cmd" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var configs = McpConfigSourceFactory.LoadAndMerge(new[] { source });

        configs.Should().ContainKey("local");
    }

    [Test]
    public async Task LoadAndMerge_ExcludesSelfServer()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".mcp.json"), """
            {
                "mcpServers": {
                    "repoql": { "command": "self" },
                    "other": { "command": "other" }
                }
            }
            """);

        var source = new DirectoryMcpConfigSource(_tempDir);
        var configs = McpConfigSourceFactory.LoadAndMerge(new[] { source }, selfServerName: "repoql");

        configs.Should().NotContainKey("repoql");
        configs.Should().ContainKey("other");
    }

    [Test]
    public async Task LoadAndMerge_HigherPriorityOverrides()
    {
        var tempDir2 = Path.Combine(Path.GetTempPath(), "repoql-factory-test2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir2);

        try
        {
            // Low priority source
            await File.WriteAllTextAsync(Path.Combine(_tempDir, ".mcp.json"), """
                {
                    "mcpServers": {
                        "shared": { "command": "low-priority" }
                    }
                }
                """);

            // High priority source
            await File.WriteAllTextAsync(Path.Combine(tempDir2, ".mcp.json"), """
                {
                    "mcpServers": {
                        "shared": { "command": "high-priority" }
                    }
                }
                """);

            var lowPrioritySource = new TestConfigSource(_tempDir, priority: 100);
            var highPrioritySource = new TestConfigSource(tempDir2, priority: 200);

            var configs = McpConfigSourceFactory.LoadAndMerge(
                new IMcpConfigSource[] { lowPrioritySource, highPrioritySource });

            configs["shared"].Command.Should().Be("high-priority");
        }
        finally
        {
            Directory.Delete(tempDir2, recursive: true);
        }
    }

    #endregion

    #region GetAllAgentTypes

    [Test]
    public async Task GetAllAgentTypes_ReturnsImplementedAgents()
    {
        var types = McpConfigSourceFactory.GetAllAgentTypes();

        types.Should().Contain(AgentType.ClaudeCode);
        types.Should().Contain(AgentType.ClaudeDesktop);
    }

    #endregion

    /// <summary>
    /// Test helper to control priority
    /// </summary>
    private sealed class TestConfigSource : IMcpConfigSource
    {
        private readonly string _directory;
        private readonly int _priority;

        public TestConfigSource(string directory, int priority)
        {
            _directory = directory;
            _priority = priority;
        }

        public string Name => $"Test: {_directory}";
        public int Priority => _priority;

        public IReadOnlyDictionary<string, McpServerConfig> LoadConfigs()
        {
            return new DirectoryMcpConfigSource(_directory).LoadConfigs();
        }
    }
}
