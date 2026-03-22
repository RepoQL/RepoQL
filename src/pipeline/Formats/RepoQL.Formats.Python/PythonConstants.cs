namespace RepoQL.Formats.Python;

/// <summary>
/// Constants for Python format node kinds, edge types, and property keys.
/// </summary>
public static class PythonConstants
{
    public static class NodeKinds
    {
        public const string Type = "py.type";
        public const string Member = "py.member";
        public const string Function = "py.function";
    }

    public static class EdgeTypes
    {
        public const string HasPart = "HAS_PART";
        public const string Extends = "EXTENDS";
        public const string Imports = "IMPORTS";
    }

    public static class AnnotationKinds
    {
        public const string Metaprogramming = "python.metaprogramming";
        public const string Framework = "python.framework";
    }

    public static class PropertyKeys
    {
        public const string Name = "name";
        public const string QualifiedName = "qualified_name";
        public const string Kind = "kind";
        public const string TypeKind = "type_kind";
        public const string Extends = "extends";
        public const string Metaclass = "metaclass";
        public const string Namespace = "namespace";
        public const string Decorators = "decorators";
        public const string IsAbstract = "is_abstract";
        public const string Docstring = "docstring";
        public const string Slots = "slots";
        public const string Variables = "variables";
        public const string Constants = "constants";
        public const string TypeAliases = "type_aliases";
        public const string AllExports = "all_exports";
        public const string Language = "language";
        public const string LineCount = "line_count";
        public const string ByteSize = "byte_size";
        public const string Role = "role";
        public const string DeclaringType = "declaring_type";
        public const string Accessibility = "accessibility";
        public const string IsStatic = "is_static";
        public const string IsClassmethod = "is_classmethod";
        public const string IsAsync = "is_async";
        public const string IsGenerator = "is_generator";
        public const string UsesAsyncWith = "uses_async_with";
        public const string UsesAsyncFor = "uses_async_for";
        public const string Parameters = "parameters";
        public const string ReturnType = "return_type";
        public const string IsGenerated = "is_generated";
        public const string Generator = "generator";
        public const string IsOverload = "is_overload";
    }
}
