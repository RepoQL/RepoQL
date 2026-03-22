namespace RepoQL.Formats.PHP.Surface;

/// <summary>
/// Parsed structural surface of a PHP source file.
///
/// Purpose: Carry all semantic information extracted by tree-sitter through to the loader for materialization.
///
/// Complexity: Positional record containing lists of all top-level declarations plus parse statistics.
/// </summary>
public sealed record PhpDocumentSurface(
    string? Namespace,
    PhpByteRange? NamespaceSpan,
    IReadOnlyList<PhpUseInfo> UseStatements,
    IReadOnlyList<PhpClassInfo> Classes,
    IReadOnlyList<PhpInterfaceInfo> Interfaces,
    IReadOnlyList<PhpTraitInfo> Traits,
    IReadOnlyList<PhpEnumInfo> Enums,
    IReadOnlyList<PhpFunctionInfo> Functions,
    PhpParseStats Stats,
    int ErrorNodeCount);

public sealed record PhpByteRange(int StartByte, int EndByte);

public sealed record PhpParseStats(
    int LineCount,
    int ClassCount,
    int InterfaceCount,
    int TraitCount,
    int EnumCount,
    int FunctionCount,
    int MethodCount);

public sealed record PhpUseInfo(
    string Name,
    string? Alias,
    PhpByteRange Span);

public sealed record PhpClassInfo(
    string Name,
    string? Namespace,
    bool IsAbstract,
    bool IsFinal,
    bool IsReadonly,
    string? Extends,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> UsesTraits,
    IReadOnlyList<PhpMethodInfo> Methods,
    IReadOnlyList<PhpPropertyInfo> Properties,
    IReadOnlyList<PhpConstantInfo> Constants,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpInterfaceInfo(
    string Name,
    string? Namespace,
    IReadOnlyList<string> Extends,
    IReadOnlyList<PhpMethodInfo> Methods,
    IReadOnlyList<PhpConstantInfo> Constants,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpTraitInfo(
    string Name,
    string? Namespace,
    IReadOnlyList<PhpMethodInfo> Methods,
    IReadOnlyList<PhpPropertyInfo> Properties,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpEnumInfo(
    string Name,
    string? Namespace,
    string? BackedType,
    IReadOnlyList<string> Implements,
    IReadOnlyList<PhpEnumCaseInfo> Cases,
    IReadOnlyList<PhpMethodInfo> Methods,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpEnumCaseInfo(
    string Name,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpFunctionInfo(
    string Name,
    string? Namespace,
    string? ReturnType,
    IReadOnlyList<string> Parameters,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpMethodInfo(
    string Name,
    string? Accessibility,
    bool IsStatic,
    bool IsAbstract,
    bool IsFinal,
    string? ReturnType,
    IReadOnlyList<string> Parameters,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpPropertyInfo(
    string Name,
    string? Accessibility,
    bool IsStatic,
    bool IsReadonly,
    string? Type,
    bool HasDefault,
    PhpByteRange Span,
    PhpByteRange NameSpan);

public sealed record PhpConstantInfo(
    string Name,
    string? Accessibility,
    PhpByteRange Span,
    PhpByteRange NameSpan);
