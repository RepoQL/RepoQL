using System.Text.Json.Serialization;

namespace RepoQL.Formats.TypeScript;

public sealed class TypeScriptParseResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("scriptKind")]
    public string ScriptKind { get; set; } = string.Empty;

    [JsonPropertyName("imports")]
    public List<TypeScriptImport> Imports { get; set; } = [];

    [JsonPropertyName("exports")]
    public List<TypeScriptExport> Exports { get; set; } = [];

    [JsonPropertyName("declarations")]
    public List<TypeScriptDeclaration> Declarations { get; set; } = [];

    [JsonPropertyName("diagnostics")]
    public List<TypeScriptDiagnostic> Diagnostics { get; set; } = [];
}

public sealed class TypeScriptImport
{
    [JsonPropertyName("specifier")]
    public string Specifier { get; set; } = string.Empty;

    [JsonPropertyName("importKind")]
    public string ImportKind { get; set; } = "value";

    [JsonPropertyName("importStyle")]
    public string ImportStyle { get; set; } = "named";

    [JsonPropertyName("span")]
    public TypeScriptSpan Span { get; set; } = new();
}

public sealed class TypeScriptExport
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("exportKind")]
    public string ExportKind { get; set; } = "named";

    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    [JsonPropertyName("span")]
    public TypeScriptSpan Span { get; set; } = new();
}

public sealed class TypeScriptDeclaration
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("declKind")]
    public string DeclKind { get; set; } = string.Empty;

    [JsonPropertyName("isExported")]
    public bool IsExported { get; set; }

    [JsonPropertyName("exportKind")]
    public string? ExportKind { get; set; }

    [JsonPropertyName("isComponent")]
    public bool IsComponent { get; set; }

    [JsonPropertyName("parameters")]
    public List<TypeScriptParameter> Parameters { get; set; } = [];

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("extends")]
    public string? Extends { get; set; }

    [JsonPropertyName("implements")]
    public List<string> Implements { get; set; } = [];

    [JsonPropertyName("typeParameters")]
    public List<string> TypeParameters { get; set; } = [];

    [JsonPropertyName("hooks")]
    public List<string> Hooks { get; set; } = [];

    [JsonPropertyName("members")]
    public List<TypeScriptMember> Members { get; set; } = [];

    [JsonPropertyName("span")]
    public TypeScriptSpan Span { get; set; } = new();
}

public sealed class TypeScriptMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("memberKind")]
    public string MemberKind { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public List<TypeScriptParameter> Parameters { get; set; } = [];

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("span")]
    public TypeScriptSpan Span { get; set; } = new();
}

public sealed class TypeScriptParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }

    [JsonPropertyName("isRest")]
    public bool IsRest { get; set; }
}

public sealed class TypeScriptSpan
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }
}

public sealed class TypeScriptDiagnostic
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
