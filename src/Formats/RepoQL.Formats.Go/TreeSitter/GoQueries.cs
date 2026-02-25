namespace RepoQL.Formats.Go.TreeSitter;

internal static class GoQueries
{
    public const string PackageClause = """
        ;; package main
        (package_clause
            (package_identifier) @package_name) @package_clause
        """;

    public const string ImportSpecs = """
        ;; import "fmt" / import alias "pkg" / import ( ... )
        (import_spec
            name: [
                (blank_identifier)
                (dot)
                (package_identifier)
            ] @import_alias
            path: [
                (interpreted_string_literal)
                (raw_string_literal)
            ] @import_path) @import_spec

        (import_spec
            !name
            path: [
                (interpreted_string_literal)
                (raw_string_literal)
            ] @import_path) @import_spec
        """;

    public const string StructDeclarations = """
        ;; type X struct { ... }
        (type_spec
            name: (type_identifier) @struct_name
            type: (struct_type) @struct_type) @struct_decl
        """;

    public const string StructFields = """
        ;; struct field declarations
        (struct_type
            (field_declaration_list
                (field_declaration) @struct_field) @field_list) @struct_type
        """;

    public const string InterfaceDeclarations = """
        ;; type X interface { ... }
        (type_spec
            name: (type_identifier) @interface_name
            type: (interface_type) @interface_type) @interface_decl
        """;

    public const string InterfaceMethods = """
        ;; interface method signatures (method_elem in current tree-sitter-go)
        (interface_type
            (_
                name: (field_identifier) @interface_method_name
                parameters: (parameter_list) @interface_method_parameters
                result: (_) @interface_method_result) @interface_method)

        (interface_type
            (_
                name: (field_identifier) @interface_method_name
                parameters: (parameter_list) @interface_method_parameters
                !result) @interface_method)
        """;

    public const string EmbeddedInterfaces = """
        ;; embedded interfaces / type terms
        (interface_type
            (type_elem (_) @embedded_interface_type) @embedded_interface)
        """;

    public const string FunctionDeclarations = """
        ;; top-level functions
        (function_declaration
            name: (identifier) @function_name
            parameters: (parameter_list) @function_parameters
            result: (_) @function_result) @function_decl

        (function_declaration
            name: (identifier) @function_name
            parameters: (parameter_list) @function_parameters
            !result) @function_decl
        """;

    public const string MethodDeclarations = """
        ;; methods with receiver
        (method_declaration
            receiver: (parameter_list) @method_receiver
            name: (field_identifier) @method_name
            parameters: (parameter_list) @method_parameters
            result: (_) @method_result) @method_decl

        (method_declaration
            receiver: (parameter_list) @method_receiver
            name: (field_identifier) @method_name
            parameters: (parameter_list) @method_parameters
            !result) @method_decl
        """;

    public const string TypeDefinitions = """
        ;; type aliases and named type definitions (exclude struct/interface in caller)
        (type_spec
            name: (type_identifier) @type_name
            type: (_) @type_underlying) @type_spec

        (type_alias
            name: (type_identifier) @type_name
            type: (_) @type_underlying) @type_alias
        """;

    public const string ConstantSpecs = """
        ;; const declarations (single and grouped)
        (const_spec
            name: (identifier) @const_name
            type: (_) @const_type
            value: (expression_list) @const_value) @const_spec

        (const_spec
            name: (identifier) @const_name
            type: (_) @const_type
            !value) @const_spec

        (const_spec
            name: (identifier) @const_name
            !type
            value: (expression_list) @const_value) @const_spec

        (const_spec
            name: (identifier) @const_name
            !type
            !value) @const_spec
        """;

    public const string VariableSpecs = """
        ;; var declarations (single and grouped)
        (var_spec
            name: (identifier) @var_name
            type: (_) @var_type
            value: (expression_list) @var_value) @var_spec

        (var_spec
            name: (identifier) @var_name
            type: (_) @var_type
            !value) @var_spec

        (var_spec
            name: (identifier) @var_name
            !type
            value: (expression_list) @var_value) @var_spec

        (var_spec
            name: (identifier) @var_name
            !type
            !value) @var_spec
        """;

    public const string Comments = """
        ;; comments include //go:* directives
        (comment) @comment
        """;

    public const string GoStatements = """
        ;; go f(...)
        (go_statement
            (_) @goroutine_expr) @goroutine_stmt
        """;

    public const string ChannelTypes = """
        ;; chan T / <-chan T / chan<- T
        (channel_type
            value: (_) @channel_value) @channel_type
        """;

    public const string SelectStatements = """
        ;; select { ... }
        (select_statement) @select_stmt
        """;
}
