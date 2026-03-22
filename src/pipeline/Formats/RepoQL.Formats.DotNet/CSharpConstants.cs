namespace RepoQL.Formats.DotNet;

/// <summary>
/// Constants for limiting output sizes in C# code analysis.
/// These values balance comprehensive information with performance and usability.
/// </summary>
internal static class CSharpLoaderConstants
{
    /// <summary>
    /// Maximum number of members to include in structure summary.
    /// Limiting prevents overwhelming output for large types while showing representative samples.
    /// </summary>
    public const int MaxMembersInStructure = 8;

    /// <summary>
    /// Maximum number of types to show in headline summary.
    /// Provides a quick overview without cluttering the display.
    /// </summary>
    public const int MaxTypesInHeadline = 3;

    /// <summary>
    /// Maximum number of public types to show in summary.
    /// Focuses on the most important API surface of the document.
    /// </summary>
    public const int MaxPublicTypesInSummary = 5;

    /// <summary>
    /// Maximum number of async members to show in summary.
    /// Highlights asynchronous patterns without overwhelming the reader.
    /// </summary>
    public const int MaxAsyncMembersInSummary = 5;

    /// <summary>
    /// Maximum number of namespaces to show in structure output.
    /// Prevents excessive nesting in large files.
    /// </summary>
    public const int MaxNamespacesInStructure = 5;

    /// <summary>
    /// Maximum number of types per namespace in structure output.
    /// Balances detail with readability.
    /// </summary>
    public const int MaxTypesPerNamespaceInStructure = 5;

    /// <summary>
    /// Maximum number of global types (types not in a namespace) in structure output.
    /// </summary>
    public const int MaxGlobalTypesInStructure = 5;
}

/// <summary>
/// Node kind identifiers for C# syntax elements in the graph structure.
/// These values are used as the 'Kind' property on Node records.
/// </summary>
internal static class CSharpNodeKinds
{
    /// <summary>
    /// Node kind for C# document nodes (the root of each file's syntax tree).
    /// </summary>
    public const string Document = "document";

    /// <summary>
    /// Node kind for C# namespace declarations.
    /// </summary>
    public const string Namespace = "csharp.namespace";

    /// <summary>
    /// Node kind for C# type declarations (class, struct, interface, enum, record).
    /// </summary>
    public const string Type = "csharp.type";

    /// <summary>
    /// Node kind for C# member declarations (method, property, field, event).
    /// </summary>
    public const string Member = "csharp.member";
}

/// <summary>
/// Edge type identifiers for relationships in the graph structure.
/// These values are used as the 'Type' property on Edge records.
/// </summary>
internal static class CSharpEdgeTypes
{
    /// <summary>
    /// Composition edge indicating hierarchical containment (e.g., namespace contains type, type contains member).
    /// </summary>
    public const string HasPart = "HAS_PART";

    /// <summary>
    /// Reference edge indicating symbol usage (e.g., method calls another method, type references another type).
    /// </summary>
    public const string UsesSymbol = "USES_SYMBOL";
}

/// <summary>
/// Property key names for C# node and edge metadata in JSON format.
/// These constants ensure consistency in property naming across the codebase.
/// </summary>
internal static class CSharpPropertyKeys
{
    // Document properties
    public const string Language = "language";
    public const string FileName = "file_name";
    public const string LineCount = "line_count";
    public const string NamespaceCount = "namespace_count";
    public const string TypeCount = "type_count";
    public const string MemberCount = "member_count";
    public const string UsingCount = "using_count";
    public const string PublicTypeCount = "public_type_count";
    public const string MethodCount = "method_count";
    public const string AsyncMemberCount = "async_member_count";
    public const string IsGenerated = "is_generated";
    public const string Generator = "generator";
    public const string HintName = "hint_name";

    // Namespace properties
    public const string Name = "name";
    public const string QualifiedName = "qualified_name";
    public const string ParentNamespaceId = "parent_namespace_id";

    // Type properties
    public const string Kind = "kind";
    public const string Namespace = "namespace";
    public const string Accessibility = "accessibility";
    public const string IsPartial = "is_partial";
    public const string IsStatic = "is_static";
    public const string IsRecord = "is_record";
    public const string BaseType = "base_type";
    public const string Interfaces = "interfaces";
    public const string SymbolKey = "symbol_key";

    // Member properties
    public const string IsAsync = "is_async";
    public const string ReturnType = "return_type";
    public const string DeclaringType = "declaring_type";
    public const string Parameters = "parameters";
    public const string HasDefault = "has_default";
    public const string Type = "type";

    // Symbol reference properties
    public const string SymbolKind = "symbol_kind";
    public const string Status = "status";

    // Diagnostic properties
    public const string Category = "category";
    public const string HelpLink = "helpLink";
    public const string Line = "line";
    public const string Column = "column";
}

/// <summary>
/// Common string values used in C# analysis.
/// </summary>
internal static class CSharpValues
{
    public const string LanguageName = "csharp";
    public const string Public = "public";
    public const string Method = "method";
    public const string StatusLocal = "local";
}
