using AwesomeAssertions;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Tests.Configuration;

internal sealed class SettingRegistryTests
{
    [Test]
    public void Build_Discovers_All_Annotated_Properties()
    {
        var registry = SettingRegistry.Build();

        registry.All.Should().NotBeEmpty();
        registry.Settings.Should().ContainKey("duckdb.memory_limit");
        registry.Settings.Should().ContainKey("embedding.mode");
        registry.Settings.Should().ContainKey("inference.api_key");
        registry.Settings.Should().ContainKey("inference.service_url");
        registry.Settings.Should().ContainKey("host.idle_grace_seconds");
        registry.Settings.Should().ContainKey("cache.size_limit");
        registry.Settings.Should().ContainKey("find.initial_candidate_limit");
        registry.Settings.Should().ContainKey("embedding.cache.enabled");
    }

    [Test]
    public void Build_Derives_Keys_From_Property_Path()
    {
        var registry = SettingRegistry.Build();

        // PascalCase → snake_case, section.property
        registry.Settings.Should().ContainKey("duckdb.read_pool_size");
        registry.Settings.Should().ContainKey("ort.intra_threads");
        registry.Settings.Should().ContainKey("mcp.include_globals");
        registry.Settings.Should().ContainKey("find.max_widening_rounds");
        registry.Settings.Should().ContainKey("embedding.cache.max_size_mb");
    }

    [Test]
    public void Build_Derives_EnvVar_Names_From_Keys()
    {
        var registry = SettingRegistry.Build();

        var duckDb = registry.TryGet("duckdb.memory_limit");
        duckDb.Should().NotBeNull();
        duckDb!.EnvVar.Should().Be("REPOQL_DUCKDB_MEMORY_LIMIT");

        var embeddingMode = registry.TryGet("embedding.mode");
        embeddingMode.Should().NotBeNull();
        embeddingMode!.EnvVar.Should().Be("REPOQL_EMBEDDING_MODE");
    }

    [Test]
    public void Build_Includes_LegacyEnvVar_When_Present()
    {
        var registry = SettingRegistry.Build();

        var memLimit = registry.TryGet("duckdb.memory_limit");
        memLimit.Should().NotBeNull();
        memLimit!.LegacyEnvVar.Should().Be("DUCKDB_MEMORY_LIMIT");

        var apiKey = registry.TryGet("inference.api_key");
        apiKey.Should().NotBeNull();
        apiKey!.LegacyEnvVar.Should().BeNull();
    }

    [Test]
    public void Build_Includes_DefaultValue_When_Present()
    {
        var registry = SettingRegistry.Build();

        var poolSize = registry.TryGet("duckdb.read_pool_size");
        poolSize.Should().NotBeNull();
        poolSize!.DefaultValue.Should().Be("2");

        var embeddingMode = registry.TryGet("embedding.mode");
        embeddingMode.Should().NotBeNull();
        embeddingMode!.DefaultValue.Should().Be("hybrid");
    }

    [Test]
    public void Build_Reads_Sensitive_Flag()
    {
        var registry = SettingRegistry.Build();

        var apiKey = registry.TryGet("inference.api_key");
        apiKey.Should().NotBeNull();
        apiKey!.Sensitive.Should().BeTrue();

        var memLimit = registry.TryGet("duckdb.memory_limit");
        memLimit.Should().NotBeNull();
        memLimit!.Sensitive.Should().BeFalse();
    }

    [Test]
    public void Build_Reads_RequiresRestart_Flag()
    {
        var registry = SettingRegistry.Build();

        var memLimit = registry.TryGet("duckdb.memory_limit");
        memLimit.Should().NotBeNull();
        memLimit!.RequiresRestart.Should().BeTrue();

        var idleGrace = registry.TryGet("host.idle_grace_seconds");
        idleGrace.Should().NotBeNull();
        idleGrace!.RequiresRestart.Should().BeFalse();
    }

    [Test]
    public void Build_Reads_Description()
    {
        var registry = SettingRegistry.Build();

        var memLimit = registry.TryGet("duckdb.memory_limit");
        memLimit.Should().NotBeNull();
        memLimit!.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryGet_Is_Case_Insensitive()
    {
        var registry = SettingRegistry.Build();

        registry.TryGet("DUCKDB.MEMORY_LIMIT").Should().NotBeNull();
        registry.TryGet("DuckDb.Memory_Limit").Should().NotBeNull();
    }

    [Test]
    [Arguments("MemoryLimit", "memory_limit")]
    [Arguments("ReadPoolSize", "read_pool_size")]
    [Arguments("IntraThreads", "intra_threads")]
    [Arguments("IdleGraceSeconds", "idle_grace_seconds")]
    [Arguments("ApiKey", "api_key")]
    [Arguments("IncludeGlobals", "include_globals")]
    [Arguments("StartTimeoutMs", "start_timeout_ms")]
    public void ToSnakeCase_Converts_PascalCase(string input, string expected)
    {
        SettingRegistry.ToSnakeCase(input).Should().Be(expected);
    }

    [Test]
    [Arguments("duckdb.memory_limit", "REPOQL_DUCKDB_MEMORY_LIMIT")]
    [Arguments("embedding.mode", "REPOQL_EMBEDDING_MODE")]
    [Arguments("ort.provider", "REPOQL_ORT_PROVIDER")]
    [Arguments("inference.api_key", "REPOQL_INFERENCE_API_KEY")]
    [Arguments("inference.service_url", "REPOQL_INFERENCE_SERVICE_URL")]
    public void DeriveEnvVar_Produces_Correct_Name(string key, string expected)
    {
        SettingRegistry.DeriveEnvVar(key).Should().Be(expected);
    }
}
