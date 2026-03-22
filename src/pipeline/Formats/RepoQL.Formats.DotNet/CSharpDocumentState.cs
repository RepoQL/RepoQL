using System.Text.Json.Nodes;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Represents the complete state of a C# document after loading, including syntax and semantic information.
/// This is stored in the DocumentModel metadata for use during materialization.
/// </summary>
internal sealed class CSharpDocumentState
{
    /// <summary>Gets or sets the unique identifier for this document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Gets or sets the content digest (hash) of the document.</summary>
    public required string Digest { get; init; }

    /// <summary>Gets or sets the size of the document in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Gets or sets the semantic media type of the document.</summary>
    public required SemanticMediaType MediaType { get; init; }

    /// <summary>Gets or sets the storage URI for the document.</summary>
    public required string StoreUri { get; init; }

    /// <summary>Gets or sets the syntax surface containing parsed declarations.</summary>
    public required CSharpDocumentSurface Surface { get; init; }

    /// <summary>Gets or sets the symbol references (calls, usages) found in the document.</summary>
    public IReadOnlyList<CSharpSymbolReference> References { get; init; } = Array.Empty<CSharpSymbolReference>();

    /// <summary>Gets or sets the compiler diagnostics (errors, warnings) for the document.</summary>
    public IReadOnlyList<CSharpDiagnostic> Diagnostics { get; init; } = Array.Empty<CSharpDiagnostic>();

    /// <summary>Gets or sets the source-generated documents associated with this document's project.</summary>
    public IReadOnlyList<CSharpGeneratedDocumentState> GeneratedDocuments { get; init; } = Array.Empty<CSharpGeneratedDocumentState>();
}

/// <summary>
/// Represents the syntax surface of a C# document, containing all parsed declarations.
/// </summary>
internal sealed class CSharpDocumentSurface
{
    /// <summary>Gets or sets the document identifier.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Gets or sets document-level properties (line count, type count, etc.).</summary>
    public required JsonObject DocumentProperties { get; init; }

    /// <summary>Gets or sets the namespace declarations in the document.</summary>
    public required IReadOnlyList<CSharpNamespaceInfo> Namespaces { get; init; }

    /// <summary>Gets or sets the type declarations (classes, structs, interfaces, etc.).</summary>
    public required IReadOnlyList<CSharpTypeInfo> Types { get; init; }

    /// <summary>Gets or sets the member declarations (methods, properties, fields, etc.).</summary>
    public required IReadOnlyList<CSharpMemberInfo> Members { get; init; }

    /// <summary>Gets or sets the using directives.</summary>
    public required IReadOnlyList<CSharpUsingInfo> Usings { get; init; }
}

/// <summary>
/// Represents a using directive in a C# document.
/// </summary>
/// <param name="NodeId">Unique identifier for the using node.</param>
/// <param name="SpanId">Identifier for the text span containing this using.</param>
/// <param name="Name">The namespace or type name being imported.</param>
/// <param name="Alias">The alias assigned to the import, if any.</param>
/// <param name="IsStatic">Whether this is a static using directive.</param>
/// <param name="Span">The location of this using in the document.</param>
internal sealed record CSharpUsingInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    string? Alias,
    bool IsStatic,
    DocumentSpan Span);

/// <summary>
/// Represents a namespace declaration in a C# document.
/// </summary>
/// <param name="NodeId">Unique identifier for the namespace node.</param>
/// <param name="SpanId">Identifier for the text span containing this namespace.</param>
/// <param name="ParentNamespaceId">Identifier of the parent namespace for nested namespaces, if any.</param>
/// <param name="Name">The simple name of the namespace.</param>
/// <param name="QualifiedName">The fully qualified name of the namespace.</param>
/// <param name="Span">The location of this namespace in the document.</param>
internal sealed record CSharpNamespaceInfo(
    Guid NodeId,
    Guid SpanId,
    Guid? ParentNamespaceId,
    string Name,
    string QualifiedName,
    DocumentSpan Span);

/// <summary>
/// Represents a type declaration (class, struct, interface, enum, record, delegate) in a C# document.
/// </summary>
/// <param name="NodeId">Unique identifier for the type node.</param>
/// <param name="SpanId">Identifier for the text span containing this type.</param>
/// <param name="NamespaceNodeId">Identifier of the containing namespace, if any.</param>
/// <param name="ParentTypeId">Identifier of the parent type for nested types, if any.</param>
/// <param name="Name">The simple name of the type.</param>
/// <param name="QualifiedName">The fully qualified name of the type.</param>
/// <param name="Kind">The kind of type (class, struct, interface, enum, record, delegate).</param>
/// <param name="Accessibility">The accessibility level (public, internal, private, etc.).</param>
/// <param name="Namespace">The namespace containing this type, if any.</param>
/// <param name="IsPartial">Whether this is a partial type declaration.</param>
/// <param name="IsStatic">Whether this is a static type.</param>
/// <param name="IsRecord">Whether this is a record type.</param>
/// <param name="BaseType">The base type name, if any.</param>
/// <param name="Interfaces">The list of implemented interface names.</param>
/// <param name="Span">The location of this type in the document.</param>
/// <param name="Modifiers">The list of type modifiers (static, partial, sealed, abstract, readonly).</param>
/// <param name="Summary">The extracted XML doc comment summary, if available.</param>
/// <param name="SymbolKey">The Roslyn symbol key for semantic lookups, if available.</param>
internal sealed record CSharpTypeInfo(
    Guid NodeId,
    Guid SpanId,
    Guid? NamespaceNodeId,
    Guid? ParentTypeId,
    string Name,
    string QualifiedName,
    string Kind,
    string Accessibility,
    string? Namespace,
    bool IsPartial,
    bool IsStatic,
    bool IsRecord,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    DocumentSpan Span,
    IReadOnlyList<string> Modifiers,
    string? Summary = null,
    string? SymbolKey = null);

/// <summary>
/// Represents a member declaration (method, property, field, event, constructor) in a C# type.
/// </summary>
/// <param name="NodeId">Unique identifier for the member node.</param>
/// <param name="SpanId">Identifier for the text span containing this member.</param>
/// <param name="DeclaringTypeId">Identifier of the type that declares this member.</param>
/// <param name="Name">The name of the member.</param>
/// <param name="Kind">The kind of member (method, property, field, event, constructor).</param>
/// <param name="Accessibility">The accessibility level (public, internal, private, etc.).</param>
/// <param name="IsStatic">Whether this is a static member.</param>
/// <param name="IsAsync">Whether this is an async member (methods only).</param>
/// <param name="ReturnType">The return type of the member, if applicable.</param>
/// <param name="DeclaringTypeDisplay">Display name of the declaring type.</param>
/// <param name="Parameters">The list of parameters for methods and constructors.</param>
/// <param name="Span">The location of this member in the document.</param>
/// <param name="Modifiers">The list of member modifiers (static, async, virtual, override, etc.).</param>
/// <param name="Summary">The extracted XML doc comment summary, if available.</param>
/// <param name="SymbolKey">The Roslyn symbol key for semantic lookups, if available.</param>
internal sealed record CSharpMemberInfo(
    Guid NodeId,
    Guid SpanId,
    Guid DeclaringTypeId,
    string Name,
    string Kind,
    string Accessibility,
    bool IsStatic,
    bool IsAsync,
    string? ReturnType,
    string? DeclaringTypeDisplay,
    IReadOnlyList<CSharpParameterInfo> Parameters,
    DocumentSpan Span,
    IReadOnlyList<string> Modifiers,
    string? Summary = null,
    string? SymbolKey = null);

/// <summary>
/// Represents a parameter in a method or constructor signature.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Type">The parameter type.</param>
/// <param name="HasDefaultValue">Whether the parameter has a default value.</param>
internal sealed record CSharpParameterInfo(
    string Name,
    string Type,
    bool HasDefaultValue);

/// <summary>
/// Represents a symbol reference (usage) in C# code, such as a method call or type reference.
/// </summary>
/// <param name="SourceNodeId">The node making the reference (the caller/user).</param>
/// <param name="Span">The location of the reference in the document.</param>
/// <param name="SymbolKey">The Roslyn symbol key identifying the referenced symbol.</param>
/// <param name="SymbolKind">The kind of symbol being referenced (Method, Type, Field, etc.).</param>
/// <param name="TargetNodeId">The node being referenced, if it's declared in the same document.</param>
internal sealed record CSharpSymbolReference(
    Guid SourceNodeId,
    DocumentSpan Span,
    string SymbolKey,
    string? SymbolKind,
    Guid? TargetNodeId);

/// <summary>
/// Represents a compiler diagnostic (error, warning, info) for a C# document.
/// </summary>
/// <param name="Id">The diagnostic ID (e.g., CS0103, CS1591).</param>
/// <param name="Message">The diagnostic message text.</param>
/// <param name="Severity">The severity level (Error, Warning, Info, Hidden).</param>
/// <param name="Category">The diagnostic category.</param>
/// <param name="HelpLink">A URL to documentation about this diagnostic, if available.</param>
/// <param name="Span">The location of the diagnostic in the document.</param>
internal sealed record CSharpDiagnostic(
    string Id,
    string Message,
    string Severity,
    string Category,
    string? HelpLink,
    DocumentSpan Span);

/// <summary>
/// Represents a source-generated C# document produced by a Roslyn source generator.
/// </summary>
/// <param name="DocumentId">Unique identifier for the generated document.</param>
/// <param name="StoreUri">Storage URI for the generated document.</param>
/// <param name="GeneratorName">The name of the source generator that produced this document.</param>
/// <param name="HintName">The hint name provided by the generator.</param>
/// <param name="Text">The generated source code text.</param>
/// <param name="FilePath">The file path associated with the generated syntax tree, if any.</param>
/// <param name="MediaType">The media type of the generated document.</param>
/// <param name="Digest">Content digest (hash) of the generated text.</param>
/// <param name="Size">Size of the generated text in bytes.</param>
/// <param name="Surface">The syntax surface of the generated document.</param>
/// <param name="References">Symbol references found in the generated document.</param>
/// <param name="Diagnostics">Diagnostics associated with the generated document.</param>
internal sealed record CSharpGeneratedDocumentState(
    Guid DocumentId,
    string StoreUri,
    string GeneratorName,
    string HintName,
    string Text,
    string? FilePath,
    SemanticMediaType MediaType,
    string Digest,
    long Size,
    CSharpDocumentSurface Surface,
    IReadOnlyList<CSharpSymbolReference> References,
    IReadOnlyList<CSharpDiagnostic> Diagnostics);
