namespace RepoQL.Formats.Cpp;

internal static class CppPropertyKeys
{
    public const string Language = "language";
    public const string LineCount = "line_count";
    public const string ByteSize = "byte_size";
    public const string StartLine = "start_line";
    public const string EndLine = "end_line";

    public const string Name = "name";
    public const string QualifiedName = "qualified_name";
    public const string Kind = "kind";
    public const string Namespace = "namespace";
    public const string Accessibility = "accessibility";
    public const string Access = "access";
    public const string Extends = "extends";
    public const string DeclaringType = "declaring_type";
    public const string ReturnType = "return_type";
    public const string Signature = "signature";
    public const string Parameters = "parameters";
    public const string Value = "value";
    public const string UnderlyingType = "underlying_type";
    public const string Target = "target";
    public const string TargetType = "target_type";
    public const string Style = "style";
    public const string Replacement = "replacement";
    public const string Predicate = "predicate";
    public const string Constraint = "constraint";
    public const string Partition = "partition";
    public const string IsExport = "is_export";
    public const string Relationship = "relationship";
    public const string IsResolved = "is_resolved";
    public const string IsTemplate = "is_template";
    public const string TemplateParams = "template_params";
    public const string BaseTemplate = "base_template";
    public const string SpecializationArgs = "specialization_args";
    public const string IsCoroutine = "is_coroutine";
    public const string BitfieldWidth = "bitfield_width";
    public const string IsFunctionPointer = "is_function_pointer";
    public const string PointedSignature = "pointed_signature";
    public const string IsVariadic = "is_variadic";
    public const string DocComment = "doc_comment";
    public const string DocTags = "doc_tags";
    public const string Attributes = "attributes";
    public const string VendorAttributes = "vendor_attributes";
    public const string IsTest = "is_test";
    public const string TestSuite = "test_suite";
    public const string TestName = "test_name";
    public const string CaughtTypes = "caught_types";
    public const string ThrownType = "thrown_type";
    public const string MacroName = "macro_name";
    public const string Context = "context";
    public const string Confidence = "confidence";
    public const string Step = "step";
    public const string Depth = "depth";

    public const string IsAbstract = "is_abstract";
    public const string IsForwardDeclaration = "is_forward_declaration";
    public const string IsScoped = "is_scoped";
    public const string IsAnonymous = "is_anonymous";
    public const string IsInline = "is_inline";
    public const string IsVirtual = "is_virtual";
    public const string IsPureVirtual = "is_pure_virtual";
    public const string IsOverride = "is_override";
    public const string IsFinal = "is_final";
    public const string IsNoexcept = "is_noexcept";
    public const string IsConstexpr = "is_constexpr";
    public const string IsStatic = "is_static";
    public const string IsConst = "is_const";
}

internal static class CppValues
{
    public const string LanguageCpp = "cpp";
    public const string LanguageC = "c";
    public const string Public = "public";
    public const string Private = "private";
    public const string Protected = "protected";
    public const string AnnotationSource = "repoql.formats.cpp";
    public const string AnalyzerAnnotationSource = "cpp-analyzer";
}

internal static class CppAnnotationRuleIds
{
    public const string ParseFailure = "cpp/parse_failure";
    public const string ParseTimeout = "cpp/parse_timeout";
    public const string GrammarLoadFailure = "cpp/grammar_load_failure";
    public const string MacroInterference = "cpp/macro_interference";
    public const string SyntaxError = "cpp/syntax_error";
    public const string TemplateComplexity = "cpp/template_complexity";
    public const string PreprocessorBoundary = "cpp/preprocessor_boundary";
    public const string ConditionalCompilation = "cpp/conditional_compilation";
    public const string UnsupportedModuleSyntax = "cpp/unsupported_module_syntax";
    public const string ExceptionHandler = "cpp/exception_handler";
    public const string ThrowExpression = "cpp/throw_expression";
    public const string AnalysisFailure = "cpp/analysis_failure";
    public const string TestFramework = "cpp/test_framework";
    public const string IncludeCycle = "cpp/include_cycle";
}
