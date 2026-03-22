using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RepoQL.Contracts.Inference;
using RepoQL.McpServer.Tools;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Derives an inference-safe read tool definition from the MCP read tool contract.
/// Complexity: Uses reflection to mirror tool metadata and strips the question modifier docs
/// so inference tool use cannot recursively trigger host-side question synthesis.
/// </summary>
internal static class InferenceReadToolDefinitionFactory
{
    private static readonly Regex QuestionModifierBlock = new(
        @"\n\s*\*\*question\*\*:.*?</MODIFIERS>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static InferenceToolDefinition Create()
    {
        var method = typeof(ReadTool).GetMethod(nameof(ReadTool.ReadAsync), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("ReadTool.ReadAsync could not be found.");
        var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>()
            ?? throw new InvalidOperationException("ReadTool.ReadAsync is missing McpServerToolAttribute.");
        var descriptionAttribute = method.GetCustomAttribute<DescriptionAttribute>()
            ?? throw new InvalidOperationException("ReadTool.ReadAsync is missing DescriptionAttribute.");

        return new InferenceToolDefinition
        {
            Name = toolAttribute.Name,
            Description = StripQuestionModifierDocumentation(descriptionAttribute.Description),
            ParametersJson = BuildParametersJson(method)
        };
    }

    private static string BuildParametersJson(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
                continue;

            var schema = new JsonObject
            {
                ["type"] = GetJsonType(parameter.ParameterType)
            };

            var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(description))
                schema["description"] = description;

            properties[parameter.Name!] = schema;

            if (!parameter.HasDefaultValue && !parameter.IsOptional)
                required.Add(parameter.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        }.ToJsonString();
    }

    internal static string StripQuestionModifierDocumentation(string description)
    {
        var withoutQuestionBlock = QuestionModifierBlock.Replace(description, "\n        </MODIFIERS>");
        var filteredLines = withoutQuestionBlock
            .Split('\n')
            .Where(line => !line.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains("Ask a focused question about specific code", StringComparison.OrdinalIgnoreCase));

        return string.Join('\n', filteredLines).Trim();
    }

    private static string GetJsonType(Type parameterType)
    {
        var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        if (type == typeof(string))
            return "string";
        if (type == typeof(bool))
            return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return "number";

        return "string";
    }
}
