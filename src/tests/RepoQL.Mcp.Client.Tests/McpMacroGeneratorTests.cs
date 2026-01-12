using System.Text.Json;
using AwesomeAssertions;

namespace RepoQL.Mcp.Client.Tests;

public class McpMacroGeneratorTests
{
    #region GenerateToolMacro

    [Test]
    public async Task GenerateToolMacro_WithNoParameters_GeneratesValidMacro()
    {
        var tool = new McpToolDefinition
        {
            ServerName = "test-server",
            ToolName = "list_items",
            Description = "Lists all items"
        };

        var result = McpMacroGenerator.GenerateToolMacro(tool);

        result.Should().Contain("CREATE OR REPLACE MACRO test_server_list_items()");
        result.Should().Contain("_mcp_call_internal('test-server', 'list_items'");
    }

    [Test]
    public async Task GenerateToolMacro_WithParameters_GeneratesParameterizedMacro()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceName": { "type": "string", "description": "The resource name" },
                    "limit": { "type": "integer" }
                },
                "required": ["resourceName"]
            }
            """).RootElement;

        var tool = new McpToolDefinition
        {
            ServerName = "dashboard",
            ToolName = "get_logs",
            Description = "Gets logs",
            InputSchema = schema
        };

        var result = McpMacroGenerator.GenerateToolMacro(tool);

        result.Should().Contain("dashboard_get_logs(");
        // SQL params use sanitized (lowercased) names, quoted for reserved keywords
        result.Should().Contain("\"resourcename\" :=");
        result.Should().Contain("\"limit\" :=");
        // JSON keys preserve original case for MCP server, SQL params use sanitized names
        result.Should().Contain("'resourceName', \"resourcename\"");
        result.Should().Contain("'limit', \"limit\"");
    }

    [Test]
    public async Task GenerateToolMacro_QuotesReservedKeywordParameters()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "offset": { "type": "integer", "description": "Start position" },
                    "count": { "type": "integer", "description": "Number of items" }
                }
            }
            """).RootElement;

        var tool = new McpToolDefinition
        {
            ServerName = "api",
            ToolName = "list",
            InputSchema = schema
        };

        var result = McpMacroGenerator.GenerateToolMacro(tool);

        // Parameter names should be quoted to handle reserved keywords
        result.Should().Contain("\"offset\" :=");
        result.Should().Contain("\"count\" :=");
        // JSON keys should use single quotes, parameter refs should use double quotes
        result.Should().Contain("'offset', \"offset\"");
        result.Should().Contain("'count', \"count\"");
    }

    [Test]
    public async Task GenerateToolMacro_SanitizesServerNameInMacroName()
    {
        var tool = new McpToolDefinition
        {
            ServerName = "my-special-server",
            ToolName = "do_thing",
            Description = "Does a thing"
        };

        var result = McpMacroGenerator.GenerateToolMacro(tool);

        // Macro name should be sanitized (underscores)
        result.Should().Contain("my_special_server_do_thing()");
        // But the UDF call should use the original server name
        result.Should().Contain("'my-special-server'");
    }

    #endregion

    #region GenerateDiscoveryMacro

    [Test]
    public async Task GenerateDiscoveryMacro_WithNoTools_ReturnsEmptyTable()
    {
        var tools = new List<McpToolDefinition>();

        var result = McpMacroGenerator.GenerateDiscoveryMacro(tools);

        result.Should().Contain("mcp_tools()");
        result.Should().Contain("WHERE false");
    }

    [Test]
    public async Task GenerateDiscoveryMacro_WithTools_ListsAllTools()
    {
        var tools = new List<McpToolDefinition>
        {
            new() { ServerName = "server1", ToolName = "tool1", Description = "First tool" },
            new() { ServerName = "server2", ToolName = "tool2", Description = "Second tool" }
        };

        var result = McpMacroGenerator.GenerateDiscoveryMacro(tools);

        result.Should().Contain("mcp_tools()");
        result.Should().Contain("'server1'");
        result.Should().Contain("'tool1'");
        result.Should().Contain("'server1_tool1'");
        result.Should().Contain("'First tool'");
        result.Should().Contain("'server2'");
        result.Should().Contain("'tool2'");
    }

    [Test]
    public async Task GenerateDiscoveryMacro_EscapesSqlInDescription()
    {
        var tools = new List<McpToolDefinition>
        {
            new() { ServerName = "server", ToolName = "tool", Description = "It's a \"quoted\" description" }
        };

        var result = McpMacroGenerator.GenerateDiscoveryMacro(tools);

        result.Should().Contain("It''s a \"quoted\" description");
    }

    #endregion

    #region GenerateMacros

    [Test]
    public async Task GenerateMacros_GeneratesAllMacrosAndDiscovery()
    {
        var tools = new List<McpToolDefinition>
        {
            new() { ServerName = "srv", ToolName = "list", Description = "List items" },
            new() { ServerName = "srv", ToolName = "get", Description = "Get item" }
        };

        var result = McpMacroGenerator.GenerateMacros(tools);

        result.Should().Contain("srv_list()");
        result.Should().Contain("srv_get()");
        result.Should().Contain("mcp_tools()");
    }

    #endregion

    #region SanitizeName

    [Test]
    [Arguments("simple", "simple")]
    [Arguments("with-hyphen", "with_hyphen")]
    [Arguments("with.dot", "with_dot")]
    [Arguments("with space", "with_space")]
    [Arguments("MixedCase", "mixedcase")]
    [Arguments("123starts_with_number", "_123starts_with_number")]
    [Arguments("already_valid", "already_valid")]
    public async Task SanitizeName_TransformsCorrectly(string input, string expected)
    {
        var result = McpMacroGenerator.SanitizeName(input);

        result.Should().Be(expected);
    }

    #endregion

    #region ExtractParameters

    [Test]
    public async Task ExtractParameters_WithNullSchema_ReturnsEmpty()
    {
        var result = McpMacroGenerator.ExtractParameters(null);

        result.Should().BeEmpty();
    }

    [Test]
    public async Task ExtractParameters_WithEmptyProperties_ReturnsEmpty()
    {
        var schema = JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        result.Should().BeEmpty();
    }

    [Test]
    public async Task ExtractParameters_ExtractsAllProperties()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" },
                    "count": { "type": "integer" },
                    "enabled": { "type": "boolean" }
                }
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        result.Should().HaveCount(3);
        result.Select(p => p.Name).Should().Contain("name");
        result.Select(p => p.Name).Should().Contain("count");
        result.Select(p => p.Name).Should().Contain("enabled");
    }

    [Test]
    public async Task ExtractParameters_MarksRequiredParameters()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "required_param": { "type": "string" },
                    "optional_param": { "type": "string" }
                },
                "required": ["required_param"]
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        var requiredParam = result.First(p => p.Name == "required_param");
        var optionalParam = result.First(p => p.Name == "optional_param");

        requiredParam.Required.Should().BeTrue();
        optionalParam.Required.Should().BeFalse();
    }

    [Test]
    public async Task ExtractParameters_SortsRequiredFirst()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "z_optional": { "type": "string" },
                    "a_required": { "type": "string" },
                    "m_optional": { "type": "string" }
                },
                "required": ["a_required"]
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        result[0].Name.Should().Be("a_required");
        result[0].Required.Should().BeTrue();
    }

    [Test]
    public async Task ExtractParameters_ExtractsDescription()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "param": { "type": "string", "description": "A helpful description" }
                }
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        result[0].Description.Should().Be("A helpful description");
    }

    [Test]
    public async Task ExtractParameters_SanitizesParameterNames()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource-name": { "type": "string" }
                }
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        result[0].Name.Should().Be("resource_name");
        result[0].OriginalName.Should().Be("resource-name");
    }

    [Test]
    public async Task ExtractParameters_PreservesOriginalCamelCaseName()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "appId": { "type": "string" },
                    "searchTerm": { "type": "string" }
                }
            }
            """).RootElement;

        var result = McpMacroGenerator.ExtractParameters(schema);

        // SQL names are lowercased
        result.Select(p => p.Name).Should().Contain("appid");
        result.Select(p => p.Name).Should().Contain("searchterm");
        // Original names preserve case for MCP
        result.Select(p => p.OriginalName).Should().Contain("appId");
        result.Select(p => p.OriginalName).Should().Contain("searchTerm");
    }

    #endregion
}
