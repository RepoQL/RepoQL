namespace RepoQL.Formats.Go;

internal static class GoNodeKinds
{
    public const string Document = "document";
    public const string Type = "go.type";
    public const string Member = "go.member";
    public const string Function = "go.function";
}

internal static class GoEdgeTypes
{
    public const string HasPart = "HAS_PART";
    public const string Imports = "IMPORTS";
    public const string Embeds = "EMBEDS";
    public const string DependsOn = "DEPENDS_ON";
    public const string Implements = "IMPLEMENTS";
}

internal static class GoPropertyKeys
{
    public const string Language = "language";
    public const string LineCount = "line_count";
    public const string ByteSize = "byte_size";
    public const string PackageName = "package_name";
    public const string ModulePath = "module_path";
    public const string GoVersion = "go_version";
    public const string Toolchain = "toolchain";
    public const string Version = "version";
    public const string Indirect = "indirect";

    public const string Name = "name";
    public const string QualifiedName = "qualified_name";
    public const string Kind = "kind";
    public const string Accessibility = "accessibility";
    public const string DeclaringType = "declaring_type";
    public const string IsStatic = "is_static";
    public const string Parameters = "parameters";
    public const string ReturnType = "return_type";
    public const string Signature = "signature";
    public const string IsExported = "is_exported";
    public const string Receiver = "receiver";
    public const string ReceiverType = "receiver_type";
    public const string IsPointerReceiver = "is_pointer_receiver";
    public const string IsInit = "is_init";
    public const string TestKind = "test_kind";
    public const string TestsSymbol = "tests_symbol";
    public const string Tag = "tag";
    public const string FieldType = "field_type";
    public const string IsEmbedded = "is_embedded";
    public const string UnderlyingType = "underlying_type";
    public const string ConstType = "const_type";
    public const string ConstValue = "const_value";
    public const string EnumType = "enum_type";
    public const string VarType = "var_type";
    public const string VarValue = "var_value";
    public const string IsSentinelError = "is_sentinel_error";
    public const string IsInterfaceAssertion = "is_interface_assertion";
    public const string AssertedInterface = "asserted_interface";
    public const string AssertedType = "asserted_type";
    public const string Target = "target";
    public const string ReceiverKind = "receiver_kind";
    public const string IsStdlib = "is_stdlib";
    public const string Alias = "alias";
    public const string ImportCategory = "import_category";
    public const string OldPath = "old_path";
    public const string OldVersion = "old_version";
    public const string NewPath = "new_path";
    public const string NewVersion = "new_version";
    public const string IsLocalPath = "is_local_path";
    public const string Path = "path";
}

internal static class GoAnnotationKinds
{
    public const string EnumBlock = "go.enum_block";
    public const string BuildConstraint = "go.build_constraint";
    public const string Generate = "go.generate";
    public const string Embed = "go.embed";
    public const string Linkname = "go.linkname";
    public const string Goroutine = "go.goroutine";
    public const string Channel = "go.channel";
    public const string Select = "go.select";
    public const string Test = "go.test";
    public const string InterfaceAssertion = "go.interface_assertion";
    public const string InterfaceSatisfaction = "go.interface_satisfaction";
    public const string GoModReplace = "go.mod_replace";
    public const string GoModRetract = "go.mod_retract";
    public const string GoWorkUse = "go.work_use";
}

internal static class GoValues
{
    public const string LanguageName = "go";
    public const string GoModLanguageName = "go.mod";
    public const string GoWorkLanguageName = "go.work";
    public const string Public = "public";
    public const string Private = "private";
    public const string AnnotationSource = "repoql.formats.go";
}
