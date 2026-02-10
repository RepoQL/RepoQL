namespace RepoQL.Formats.Ruby;

internal static class RubyNodeKinds
{
    public const string Document = "document";
    public const string Type = "rb.type";
    public const string Member = "rb.member";
    public const string Function = "rb.function";
    public const string Constant = "rb.constant";
    public const string Property = "rb.property";
}

internal static class RubyEdgeTypes
{
    public const string HasPart = "HAS_PART";
    public const string Extends = "EXTENDS";
    public const string Includes = "INCLUDES";
    public const string Prepends = "PREPENDS";
    public const string ExtendsModule = "EXTENDS_MODULE";
    public const string Requires = "REQUIRES";
    public const string Aliases = "ALIASES";
    public const string Associates = "ASSOCIATES";
}

internal static class RubyPropertyKeys
{
    public const string Language = "language";
    public const string LineCount = "line_count";
    public const string ByteSize = "byte_size";

    public const string Name = "name";
    public const string QualifiedName = "qualified_name";
    public const string Kind = "kind";
    public const string Namespace = "namespace";
    public const string Accessibility = "accessibility";
    public const string Extends = "extends";
    public const string DeclaringType = "declaring_type";
    public const string IsStatic = "is_static";
    public const string Parameters = "parameters";
    public const string ReturnType = "return_type";
    public const string AcceptsBlock = "accepts_block";
    public const string Receiver = "receiver";
    public const string IsGenerated = "is_generated";
    public const string Generator = "generator";
    public const string DelegateTo = "delegate_to";
    public const string AccessorType = "accessor_type";
    public const string Association = "association";
    public const string Options = "options";
    public const string IsReopening = "is_reopening";
    public const string Target = "target";
    public const string Ordinal = "ordinal";
    public const string Path = "path";
    public const string IsRelative = "is_relative";
    public const string AliasType = "alias_type";
}

internal static class RubyValues
{
    public const string LanguageName = "ruby";
    public const string Public = "public";
    public const string Protected = "protected";
    public const string Private = "private";
}
