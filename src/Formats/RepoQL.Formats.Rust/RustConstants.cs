namespace RepoQL.Formats.Rust;

internal static class RustNodeKinds
{
    public const string Document = "document";
    public const string Type = "rs.type";
    public const string Member = "rs.member";
    public const string Function = "rs.function";
    public const string Macro = "rs.macro";
    public const string Module = "rs.module";
}

internal static class RustEdgeTypes
{
    public const string HasPart = "HAS_PART";
    public const string Implements = "IMPLEMENTS";
    public const string Extends = "EXTENDS";
    public const string Derives = "DERIVES";
    public const string Imports = "IMPORTS";
}

internal static class RustPropertyKeys
{
    public const string Language = "language";
    public const string LineCount = "line_count";
    public const string ByteSize = "byte_size";

    public const string Name = "name";
    public const string QualifiedName = "qualified_name";
    public const string Kind = "kind";
    public const string Accessibility = "accessibility";
    public const string Extends = "extends";
    public const string DeclaringType = "declaring_type";
    public const string IsStatic = "is_static";
    public const string Parameters = "parameters";
    public const string ReturnType = "return_type";
    public const string IsAsync = "is_async";
    public const string IsUnsafe = "is_unsafe";
    public const string IsConst = "is_const";
    public const string SelfKind = "self_kind";
    public const string ImplTrait = "impl_trait";
    public const string IsTest = "is_test";
    public const string Generics = "generics";
    public const string WhereClause = "where_clause";
    public const string Derives = "derives";
    public const string Fields = "fields";
    public const string Variants = "variants";
    public const string AssociatedTypes = "associated_types";
    public const string AssociatedConsts = "associated_consts";
    public const string IsStub = "is_stub";
    public const string IsAuto = "is_auto";
    public const string IsInline = "is_inline";
    public const string IsMutable = "is_mutable";
    public const string Implements = "implements";
    public const string Target = "target";
    public const string Cfg = "cfg";
    public const string MustUse = "must_use";
    public const string IsDeprecated = "is_deprecated";
    public const string Attributes = "attributes";
    public const string Path = "path";
    public const string Alias = "alias";
    public const string IsGlob = "is_glob";
    public const string IsPub = "is_pub";
}

internal static class RustValues
{
    public const string LanguageName = "rust";
}

internal static class RustAnnotationKinds
{
    public const string MacroExpansion = "rs.macro_expansion";
}

internal static class RustAnnotationRuleIds
{
    public const string Derive = "derive";
}

internal static class RustAnnotationSources
{
    public const string RustLoader = "repoql.formats.rust";
}

internal static class RustMacroFilters
{
    public static readonly HashSet<string> NonStructuralMacroInvocations = new(StringComparer.Ordinal)
    {
        "println",
        "eprintln",
        "dbg",
        "assert",
        "assert_eq",
        "assert_ne",
        "todo",
        "unimplemented",
        "panic",
        "format",
        "write",
        "writeln",
        "log::info",
        "log::warn",
        "log::error",
        "log::debug",
        "log::trace",
        "vec",
        "cfg",
        "env",
        "include",
        "include_str",
        "include_bytes",
        "concat",
        "stringify",
        "file",
        "line",
        "column",
        "module_path"
    };

    public static readonly HashSet<string> BuiltInNonGenerativeAttributes = new(StringComparer.Ordinal)
    {
        "allow",
        "deny",
        "warn",
        "cfg",
        "cfg_attr",
        "inline",
        "must_use",
        "deprecated",
        "doc",
        "repr",
        "path",
        "link",
        "no_mangle",
        "export_name",
        "derive",
        "test",
        "bench",
        "global_allocator",
        "track_caller",
        "cold",
        "ignore",
        "should_panic",
        "automatically_derived",
        "non_exhaustive"
    };
}
