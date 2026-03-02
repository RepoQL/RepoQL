namespace RepoQL.Formats.PHP.TreeSitter;

/// <summary>
/// Tree-sitter query constants for extracting PHP structural elements.
///
/// Purpose: Centralize all S-expression queries used by PhpTreeSitterClient.
///
/// Complexity: One constant per construct — classes, interfaces, traits, enums, functions,
/// methods, namespaces, use statements, properties, constants, and enum cases.
/// </summary>
internal static class PhpQueries
{
    public const string ClassDeclarations = """
        (class_declaration
            name: (name) @class_name) @class_node
        """;

    public const string InterfaceDeclarations = """
        (interface_declaration
            name: (name) @interface_name) @interface_node
        """;

    public const string TraitDeclarations = """
        (trait_declaration
            name: (name) @trait_name) @trait_node
        """;

    public const string EnumDeclarations = """
        (enum_declaration
            name: (name) @enum_name) @enum_node
        """;

    public const string FunctionDefinitions = """
        (function_definition
            name: (name) @function_name) @function_node
        """;

    public const string MethodDeclarations = """
        (method_declaration
            name: (name) @method_name) @method_node
        """;

    public const string NamespaceDefinitions = """
        (namespace_definition
            (namespace_name) @namespace_name) @namespace_node
        """;

    public const string UseDeclarations = """
        (namespace_use_declaration
            (namespace_use_clause) @use_clause) @use_node
        """;

    public const string TraitUseDeclarations = """
        (use_declaration
            (name) @trait_use_name) @trait_use_node
        """;

    public const string PropertyDeclarations = """
        (property_declaration) @property_node
        """;

    public const string ConstDeclarations = """
        (const_declaration) @const_node
        """;

    public const string EnumCases = """
        (enum_case
            name: (name) @case_name) @case_node
        """;
}
