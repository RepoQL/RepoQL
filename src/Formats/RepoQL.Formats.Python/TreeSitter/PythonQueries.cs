namespace RepoQL.Formats.Python.TreeSitter;

internal static class PythonQueries
{
    public const string ClassDeclarations = """
        ;; Class declarations with optional bases and class body
        (class_definition
            name: (identifier) @class_name
            superclasses: (argument_list)? @superclasses
            body: (block) @class_body) @class_node
        """;

    public const string FunctionDeclarations = """
        ;; Function declarations with optional return type
        (function_definition
            name: (identifier) @function_name
            parameters: (parameters) @params
            return_type: (type)? @return_type
            body: (block) @function_body) @function_node
        """;

    public const string DecoratedDefinitions = """
        ;; Decorated definitions attach decorators to classes/functions
        (decorated_definition
            (decorator) @decorator
            definition: (_) @definition) @decorated_definition
        """;

    public const string ImportStatements = """
        ;; import x, import x as y
        (import_statement
            name: (dotted_name) @module_name) @import_statement

        (import_statement
            name: (aliased_import
                name: (dotted_name) @module_name
                alias: (identifier) @alias)) @import_statement
        """;

    public const string ImportFromStatements = """
        ;; from x import a, b / from .x import y / from x import *
        (import_from_statement
            module_name: [
                (dotted_name) @module_name
                (relative_import) @module_name
            ]
            name: (dotted_name) @import_name) @import_from_statement

        (import_from_statement
            module_name: [
                (dotted_name) @module_name
                (relative_import) @module_name
            ]
            name: (aliased_import
                name: (dotted_name) @import_name
                alias: (identifier) @import_alias)) @import_from_statement

        (import_from_statement
            module_name: [
                (dotted_name) @module_name
                (relative_import) @module_name
            ]
            (wildcard_import) @star_import) @import_from_statement
        """;

    public const string ModuleLevelAssignments = """
        ;; Module-level assignments for constants, __all__, and TypeAlias patterns
        (module
            (expression_statement
                (assignment
                    left: (identifier) @name) @assignment_node))
        """;

    public const string SelfAttributeAssignments = """
        ;; self.x = ... assignments inside methods
        (assignment
            left: (attribute
                object: (identifier) @self_object
                attribute: (identifier) @attribute_name)
            (#eq? @self_object "self")) @self_assignment
        """;

    public const string TypeAliasStatements = """
        ;; type X = Y
        (type_alias_statement
            left: (type) @alias_left
            right: (type) @alias_right) @type_alias_statement
        """;

    public const string YieldSites = """
        ;; yield / yield from
        (yield) @yield_site
        """;

    public const string AsyncWithSites = """
        ;; async with statements
        (with_statement
            "async" @async_keyword) @async_with_site
        """;

    public const string AsyncForSites = """
        ;; async for statements
        (for_statement
            "async" @async_keyword) @async_for_site
        """;

    public const string MetaprogrammingCalls = """
        ;; exec/eval/type/setattr/__import__/import_module calls
        (call
            function: [
                (identifier) @meta_function
                (attribute
                    attribute: (identifier) @meta_function)
            ]
            arguments: (_) @arguments
            (#match? @meta_function "^(exec|eval|type|setattr|__import__|import_module)$")) @meta_call
        """;

    public const string DunderDefinitions = """
        ;; __getattr__ and __dir__ definitions — PEP 562 at module level, dynamic access at class level
        (function_definition
            name: (identifier) @method_name
            (#match? @method_name "^(__getattr__|__dir__)$")) @method_node
        """;

    public const string FrameworkFieldPatterns = """
        ;; Class-level ORM/Pydantic field patterns
        (assignment
            left: (identifier) @field_name
            right: (call
                function: (_) @function_expr
                arguments: (_) @call_arguments) @call_node) @assignment_node
        """;
}
