namespace RepoQL.Formats.PHP;

/// <summary>
/// Node kind identifiers for PHP syntax elements in the graph structure.
/// </summary>
internal static class PHPNodeKinds
{
    public const string Document = "document";
    public const string Namespace = "php.namespace";
    public const string Use = "php.use";
    public const string Type = "php.type";
    public const string Function = "php.function";
    public const string Member = "php.member";
    public const string Property = "php.property";
    public const string Constant = "php.constant";
    public const string EnumCase = "php.enum_case";
}

/// <summary>
/// Edge type identifiers for relationships in the graph structure.
/// </summary>
internal static class PHPEdgeTypes
{
    /// <summary>
    /// Composition edge indicating hierarchical containment.
    /// </summary>
    public const string HasPart = "HAS_PART";

    /// <summary>
    /// Class extends another class.
    /// </summary>
    public const string Extends = "EXTENDS";

    /// <summary>
    /// Class implements an interface.
    /// </summary>
    public const string Implements = "IMPLEMENTS";

    /// <summary>
    /// Class or trait uses another trait.
    /// </summary>
    public const string UsesTrait = "USES_TRAIT";
}

/// <summary>
/// Property key names for PHP node metadata in JSON format.
/// </summary>
internal static class PHPPropertyKeys
{
    // Document properties
    public const string Language = "language";
    public const string FileName = "file_name";
    public const string LineCount = "line_count";
    public const string ByteSize = "byte_size";

    // Namespace properties
    public const string Name = "name";
    public const string QualifiedName = "qualified_name";

    // Type properties
    public const string Kind = "kind";
    public const string Namespace = "namespace";
    public const string Accessibility = "accessibility";
    public const string IsAbstract = "is_abstract";
    public const string IsFinal = "is_final";
    public const string IsReadonly = "is_readonly";
    public const string Extends = "extends";
    public const string Interfaces = "interfaces";
    public const string Traits = "traits";

    // Member properties
    public const string DeclaringType = "declaring_type";
    public const string IsStatic = "is_static";
    public const string ReturnType = "return_type";
    public const string Parameters = "parameters";
    public const string Type = "type";
    public const string HasDefault = "has_default";
    public const string Value = "value";

    // Enum properties
    public const string BackedType = "backed_type";
}

/// <summary>
/// Common string values used in PHP analysis.
/// </summary>
internal static class PHPValues
{
    public const string LanguageName = "php";
    public const string Public = "public";
    public const string Protected = "protected";
    public const string Private = "private";
}
