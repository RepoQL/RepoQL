using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.ConsoleApp.CommandImplementations;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Core.Configuration;

namespace RepoQL.Cli.Tests.CommandFramework;

[NotInParallel(nameof(ConfigCommandTests))]
internal sealed class ConfigCommandTests : IDisposable
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _tempDir;
    private readonly string _repoRoot;
    private readonly string _userConfigDir;
    private readonly SettingRegistry _registry;
    private readonly Dictionary<string, string?> _savedEnvVars;
    private readonly ResolvedConfig _resolved;
    private readonly ConfigCommand _command;

    private string LocalConfigPath => Path.Combine(_repoRoot, ".repoql", "config.json");
    private string RepoConfigPath => Path.Combine(_repoRoot, ".repoql.json");
    private string UserConfigPath => Path.Combine(_userConfigDir, "config.json");

    public ConfigCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repoql-config-command-tests-" + Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_tempDir, "repo");
        _userConfigDir = Path.Combine(_tempDir, "user-home", ".repoql");

        Directory.CreateDirectory(Path.Combine(_repoRoot, ".repoql"));
        Directory.CreateDirectory(_userConfigDir);

        _registry = SettingRegistry.Build();
        _savedEnvVars = SnapshotConfigEnvironment(_registry);
        ClearEnvironmentValues(_savedEnvVars.Keys);
        _resolved = ConfigurationLoader.Load(_registry, _repoRoot, userConfigDir: _userConfigDir);
        _command = new ConfigCommand(_registry, _resolved, new EnvironmentContext(_repoRoot));
    }

    public void Dispose()
    {
        RestoreEnvironmentValues(_savedEnvVars);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public async Task List_Includes_All_Registered_Settings()
    {
        var result = await _command.List(CancellationToken.None);

        result.IsError.Should().BeFalse();

        var settingLineCount = result.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(" = ", StringComparison.Ordinal));

        settingLineCount.Should().Be(_registry.All.Count());
    }

    [Test]
    public async Task List_Masks_Sensitive_Values()
    {
        var set = await _command.Set("inference.api_key", "abcd1234yz", "local", CancellationToken.None);
        set.IsError.Should().BeFalse();

        var list = await _command.List(CancellationToken.None);
        list.IsError.Should().BeFalse();
        list.Text.Should().Contain("abcd****yz");
        list.Text.Should().NotContain("abcd1234yz");
    }

    [Test]
    public async Task Set_And_Read_RoundTrip_Local_Scope()
    {
        var set = await _command.Set("duckdb.read_pool_size", "3", null, CancellationToken.None);
        set.IsError.Should().BeFalse();

        var read = await _command.Read("duckdb.read_pool_size", CancellationToken.None);
        read.IsError.Should().BeFalse();
        read.Text.Should().Contain("Value:          3");
        read.Text.Should().Contain("Source:         local");

        ReadInt(LocalConfigPath, "duckdb", "read_pool_size").Should().Be(3);
    }

    [Test]
    public async Task Set_And_Read_RoundTrip_Repo_Scope()
    {
        var set = await _command.Set("embedding.mode", "none", "repo", CancellationToken.None);
        set.IsError.Should().BeFalse();

        var read = await _command.Read("embedding.mode", CancellationToken.None);
        read.IsError.Should().BeFalse();
        read.Text.Should().Contain("Value:          none");
        read.Text.Should().Contain("Source:         repo");

        ReadString(RepoConfigPath, "embedding", "mode").Should().Be("none");
    }

    [Test]
    public async Task Set_And_Read_RoundTrip_User_Scope()
    {
        var set = await _command.Set("host.idle_grace_seconds", "77", "user", CancellationToken.None);
        set.IsError.Should().BeFalse();

        var read = await _command.Read("host.idle_grace_seconds", CancellationToken.None);
        read.IsError.Should().BeFalse();
        read.Text.Should().Contain("Value:          77");
        read.Text.Should().Contain("Source:         user");

        ReadInt(UserConfigPath, "host", "idle_grace_seconds").Should().Be(77);
    }

    [Test]
    public async Task Set_Rejects_Sensitive_Key_At_Repo_Scope()
    {
        var result = await _command.Set("inference.api_key", "top-secret", "repo", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("sensitive");
        result.Text.Should().Contain("Use local or user scope");
        File.Exists(RepoConfigPath).Should().BeFalse();
    }

    [Test]
    public async Task Set_Rejects_Invalid_Value_For_Int_Setting()
    {
        var result = await _command.Set("duckdb.read_pool_size", "not-an-int", "local", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("expected integer");
    }

    [Test]
    public async Task Set_Invalid_Scope_Lists_Valid_Scopes()
    {
        var result = await _command.Set("duckdb.read_pool_size", "3", "workspace", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Expected local, repo, or user.");
    }

    [Test]
    public async Task Set_Restart_Setting_Suggests_Host_Restart()
    {
        var result = await _command.Set("duckdb.threads", "4", "local", CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Restart required: yes (run ::host.restart)");
    }

    [Test]
    public async Task Reset_Removes_Key_And_Reloads()
    {
        var set = await _command.Set("duckdb.read_pool_size", "5", "local", CancellationToken.None);
        set.IsError.Should().BeFalse();

        var reset = await _command.Reset("duckdb.read_pool_size", "local", CancellationToken.None);
        reset.IsError.Should().BeFalse();

        var read = await _command.Read("duckdb.read_pool_size", CancellationToken.None);
        read.IsError.Should().BeFalse();
        read.Text.Should().Contain("Source:         default");

        HasValue(LocalConfigPath, "duckdb", "read_pool_size").Should().BeFalse();
    }

    [Test]
    public async Task Reset_Accepts_Leading_Dash_Key()
    {
        var set = await _command.Set("duckdb.read_pool_size", "6", "local", CancellationToken.None);
        set.IsError.Should().BeFalse();

        var reset = await _command.Reset("-duckdb.read_pool_size", "local", CancellationToken.None);
        reset.IsError.Should().BeFalse();

        HasValue(LocalConfigPath, "duckdb", "read_pool_size").Should().BeFalse();
    }

    [Test]
    public async Task Unknown_Key_Returns_Closest_Suggestion()
    {
        var result = await _command.Read("duckdb.memroy_limit", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Did you mean 'duckdb.memory_limit'?");
    }

    [Test]
    public async Task Set_Uses_Atomic_Write_And_Produces_Valid_Json()
    {
        var result = await _command.Set("duckdb.threads", "4", "local", CancellationToken.None);
        result.IsError.Should().BeFalse();

        File.Exists(LocalConfigPath).Should().BeTrue();
        File.Exists(LocalConfigPath + ".tmp").Should().BeFalse();

        var text = File.ReadAllText(LocalConfigPath);
        var parsed = JsonNode.Parse(text, documentOptions: JsonOptions);
        parsed.Should().BeOfType<JsonObject>();
        ReadInt(LocalConfigPath, "duckdb", "threads").Should().Be(4);
    }

    private static int ReadInt(string path, string section, string setting)
    {
        var root = ReadJson(path);
        return root[section]!.AsObject()[setting]!.GetValue<int>();
    }

    private static string ReadString(string path, string section, string setting)
    {
        var root = ReadJson(path);
        return root[section]!.AsObject()[setting]!.GetValue<string>();
    }

    private static bool HasValue(string path, string section, string setting)
    {
        if (!File.Exists(path))
            return false;

        var root = ReadJson(path);
        if (root[section] is not JsonObject sectionObj)
            return false;

        return sectionObj.ContainsKey(setting);
    }

    private static JsonObject ReadJson(string path)
    {
        var text = File.ReadAllText(path);
        var parsed = JsonNode.Parse(text, documentOptions: JsonOptions);
        parsed.Should().BeOfType<JsonObject>();
        return (JsonObject)parsed!;
    }

    private static Dictionary<string, string?> SnapshotConfigEnvironment(SettingRegistry registry)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in registry.All)
        {
            values.TryAdd(setting.EnvVar, Environment.GetEnvironmentVariable(setting.EnvVar));
            if (!string.IsNullOrWhiteSpace(setting.LegacyEnvVar))
                values.TryAdd(setting.LegacyEnvVar, Environment.GetEnvironmentVariable(setting.LegacyEnvVar));
        }

        return values;
    }

    private static void ClearEnvironmentValues(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private static void RestoreEnvironmentValues(Dictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);
    }
}
