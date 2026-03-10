using AwesomeAssertions;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Configuration;

internal sealed class ConfigurationLoaderTests
{
    private string _tempDir = null!;
    private string _repoRoot = null!;
    private string _userDir = null!;
    private Dictionary<string, string?> _savedEnvVars = null!;

    [Before(Test)]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repoql-config-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _repoRoot = Path.Combine(_tempDir, "repo");
        _userDir = Path.Combine(_tempDir, "userhome", ".repoql");
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".repoql"));

        // Clear all legacy and REPOQL_ env vars that could leak into tests
        var registry = SettingRegistry.Build();
        _savedEnvVars = new Dictionary<string, string?>();
        foreach (var def in registry.All)
        {
            SaveAndClear(def.EnvVar);
            if (def.LegacyEnvVar is not null)
                SaveAndClear(def.LegacyEnvVar);
        }
    }

    [After(Test)]
    public void Cleanup()
    {
        foreach (var (key, value) in _savedEnvVars)
            Environment.SetEnvironmentVariable(key, value);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void SaveAndClear(string envVar)
    {
        if (_savedEnvVars.ContainsKey(envVar))
            return;
        _savedEnvVars[envVar] = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, null);
    }

    private ResolvedConfig Load(string? repoRoot = null)
    {
        var registry = SettingRegistry.Build();
        return ConfigurationLoader.Load(registry, repoRoot ?? _repoRoot, userConfigDir: _userDir);
    }

    [Test]
    public void Load_With_No_Files_Returns_All_Null_Config()
    {
        var resolved = Load();

        resolved.Settings.DuckDb.MemoryLimit.Should().BeNull();
        resolved.Settings.Embedding.Mode.Should().BeNull();
        resolved.Settings.Inference.ApiKey.Should().BeNull();
    }

    [Test]
    public void Load_With_No_Files_All_Provenance_Is_Default()
    {
        var resolved = Load();

        foreach (var setting in resolved.AllResolved.Values)
            setting.Source.Should().Be(ConfigScope.Default);
    }

    [Test]
    public void Load_Reads_Local_Config_File()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 3}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_Reads_Repo_Config_File()
    {
        WriteRepoConfig("""{"embedding": {"mode": "none"}}""");

        var resolved = Load();

        resolved.Settings.Embedding.Mode.Should().Be("none");
        resolved.GetProvenance("embedding.mode")!.Source.Should().Be(ConfigScope.Repo);
    }

    [Test]
    public void Load_Reads_User_Config_File()
    {
        WriteUserConfig("""{"duckdb": {"read_pool_size": 5}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(5);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.User);
    }

    [Test]
    public void Load_Local_Overrides_Repo()
    {
        WriteRepoConfig("""{"duckdb": {"read_pool_size": 2}}""");
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 4}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(4);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_Repo_Overrides_User()
    {
        WriteUserConfig("""{"duckdb": {"read_pool_size": 1}}""");
        WriteRepoConfig("""{"duckdb": {"read_pool_size": 2}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(2);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Repo);
    }

    [Test]
    public void Load_EnvVar_Overrides_File()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 2}}""");
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_READ_POOL_SIZE", "3");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Environment);
    }

    [Test]
    public void Load_Full_Precedence_Chain()
    {
        WriteUserConfig("""{"duckdb": {"read_pool_size": 1}}""");
        WriteRepoConfig("""{"duckdb": {"read_pool_size": 2}}""");
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 3}}""");
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_READ_POOL_SIZE", "4");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(4);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Environment);
    }

    [Test]
    public void Load_Legacy_EnvVar_Used_When_New_Absent()
    {
        Environment.SetEnvironmentVariable("DUCKDB_MEMORY_LIMIT", "8GB");

        var resolved = Load();

        resolved.Settings.DuckDb.MemoryLimit.Should().Be("8GB");
        resolved.GetProvenance("duckdb.memory_limit")!.Source.Should().Be(ConfigScope.Environment);
    }

    [Test]
    public void Load_Legacy_EnvVar_Ignored_When_New_Present()
    {
        Environment.SetEnvironmentVariable("DUCKDB_MEMORY_LIMIT", "8GB");
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_MEMORY_LIMIT", "16GB");

        var resolved = Load();

        resolved.Settings.DuckDb.MemoryLimit.Should().Be("16GB");
    }

    [Test]
    public void Load_Skips_Malformed_Json_File()
    {
        WriteLocalConfig("{ this is not json }");
        WriteRepoConfig("""{"duckdb": {"read_pool_size": 3}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
    }

    [Test]
    public void Load_Ignores_Unknown_Keys()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 3}, "unknown": {"key": "value"}}""");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
    }

    [Test]
    public void Load_Handles_Boolean_Settings()
    {
        WriteLocalConfig("""{"dotnet": {"analysis": true}}""");

        var resolved = Load();

        resolved.Settings.Dotnet.Analysis.Should().BeTrue();
    }

    [Test]
    public void Load_Handles_Long_Settings()
    {
        WriteLocalConfig("""{"cache": {"size_limit": 256}}""");

        var resolved = Load();

        resolved.Settings.Cache.SizeLimit.Should().Be(256);
    }

    [Test]
    public void Load_Handles_Find_Settings_With_Double_And_Int()
    {
        WriteLocalConfig("""
        {
          "find": {
            "max_widening_rounds": 6,
            "confidence_margin": 0.075,
            "initial_candidate_limit": 128
          }
        }
        """);

        var resolved = Load();

        resolved.Settings.Find.MaxWideningRounds.Should().Be(6);
        resolved.Settings.Find.ConfidenceMargin.Should().BeApproximately(0.075, 0.000001);
        resolved.Settings.Find.InitialCandidateLimit.Should().Be(128);
        resolved.GetProvenance("find.max_widening_rounds")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_Handles_Nested_Embedding_Cache_Settings()
    {
        WriteLocalConfig("""
        {
          "embedding": {
            "cache": {
              "enabled": false,
              "path": "/tmp/repoql-cache",
              "compaction_threshold": 42,
              "max_size_mb": 123
            }
          }
        }
        """);

        var resolved = Load();

        resolved.Settings.Embedding.Cache.Enabled.Should().BeFalse();
        resolved.Settings.Embedding.Cache.Path.Should().Be("/tmp/repoql-cache");
        resolved.Settings.Embedding.Cache.CompactionThreshold.Should().Be(42);
        resolved.Settings.Embedding.Cache.MaxSizeMb.Should().Be(123);
        resolved.GetProvenance("embedding.cache.enabled")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_Handles_Json_With_Comments_And_Trailing_Commas()
    {
        WriteLocalConfig("""
        {
            // This is a comment
            "duckdb": {
                "read_pool_size": 3,
            },
        }
        """);

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
    }

    [Test]
    public void Load_Invalid_EnvVar_Int_Falls_Back_To_File()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 3}}""");
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_READ_POOL_SIZE", "not_a_number");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_With_Null_RepoRoot_Only_Reads_Env_And_User()
    {
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_READ_POOL_SIZE", "7");

        var registry = SettingRegistry.Build();
        var resolved = ConfigurationLoader.Load(registry, repoRoot: null, userConfigDir: _userDir);

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(7);
    }

    [Test]
    public void Reload_Propagates_To_Existing_Settings_Instance()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 2}}""");
        var resolved = Load();
        var originalSettings = resolved.Settings;

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(2);

        // Mutate the file and reload
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 5}}""");
        resolved.Reload(_repoRoot, userConfigDir: _userDir);

        // Same instance, updated value
        resolved.Settings.Should().BeSameAs(originalSettings);
        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(5);
        originalSettings.DuckDb.ReadPoolSize.Should().Be(5);
    }

    [Test]
    public void Reload_Updates_Provenance()
    {
        WriteLocalConfig("""{"duckdb": {"read_pool_size": 2}}""");
        var resolved = Load();

        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Local);

        // Switch to env var
        Environment.SetEnvironmentVariable("REPOQL_DUCKDB_READ_POOL_SIZE", "9");
        resolved.Reload(_repoRoot, userConfigDir: _userDir);

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(9);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Environment);
    }

    private void WriteLocalConfig(string json)
    {
        var path = Path.Combine(_repoRoot, ".repoql", "config.json");
        File.WriteAllText(path, json);
    }

    private void WriteRepoConfig(string json)
    {
        var path = Path.Combine(_repoRoot, ".repoql.json");
        File.WriteAllText(path, json);
    }

    private void WriteUserConfig(string json)
    {
        Directory.CreateDirectory(_userDir);
        File.WriteAllText(Path.Combine(_userDir, "config.json"), json);
    }
}
