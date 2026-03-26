using AwesomeAssertions;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Configuration;

internal sealed class ConfigurationLoaderTests
{
    private string _tempDir = null!;
    private string _repoRoot = null!;
    private string _userDir = null!;
    private Dictionary<string, string?> _envVars = null!;

    [Before(Test)]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repoql-config-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _repoRoot = Path.Combine(_tempDir, "repo");
        _userDir = Path.Combine(_tempDir, "userhome", ".repoql");
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".repoql"));
        _envVars = new Dictionary<string, string?>();
    }

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void SetEnvVar(string name, string? value)
        => _envVars[name] = value;

    private string? GetEnvVar(string name)
        => _envVars.TryGetValue(name, out var value) ? value : null;

    private ResolvedConfig Load(string? repoRoot = null)
    {
        var registry = SettingRegistry.Build();
        return ConfigurationLoader.Load(registry, repoRoot ?? _repoRoot, userConfigDir: _userDir, envReader: GetEnvVar);
    }

    [Test]
    public void Load_With_No_Files_Returns_All_Null_Config()
    {
        var resolved = Load();

        resolved.Settings.DuckDb.MemoryLimit.Should().BeNull();
        resolved.Settings.Embedding.ModelPath.Should().BeNull();
        resolved.Settings.Cloud.ApiKey.Should().BeNull();
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
        WriteRepoConfig("""{"embedding": {"model_path": "/models/e5.onnx"}}""");

        var resolved = Load();

        resolved.Settings.Embedding.ModelPath.Should().Be("/models/e5.onnx");
        resolved.GetProvenance("embedding.model_path")!.Source.Should().Be(ConfigScope.Repo);
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
        SetEnvVar("REPOQL_DUCKDB_READ_POOL_SIZE", "3");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Environment);
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
    public void Load_Handles_Cloud_Auth_Settings()
    {
        WriteLocalConfig("""
        {
          "cloud": {
            "client_id": "client_test",
            "auth_token": "jwt-value",
            "refresh_token": "refresh-value"
          }
        }
        """);

        var resolved = Load();

        resolved.Settings.Cloud.ClientId.Should().Be("client_test");
        resolved.Settings.Cloud.AuthToken.Should().Be("jwt-value");
        resolved.Settings.Cloud.RefreshToken.Should().Be("refresh-value");
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
        SetEnvVar("REPOQL_DUCKDB_READ_POOL_SIZE", "not_a_number");

        var resolved = Load();

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(3);
        resolved.GetProvenance("duckdb.read_pool_size")!.Source.Should().Be(ConfigScope.Local);
    }

    [Test]
    public void Load_With_Null_RepoRoot_Only_Reads_Env_And_User()
    {
        SetEnvVar("REPOQL_DUCKDB_READ_POOL_SIZE", "7");

        var registry = SettingRegistry.Build();
        var resolved = ConfigurationLoader.Load(registry, repoRoot: null, userConfigDir: _userDir, envReader: GetEnvVar);

        resolved.Settings.DuckDb.ReadPoolSize.Should().Be(7);
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
