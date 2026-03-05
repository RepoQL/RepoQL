namespace RepoQL.Formats.DotNet.TreeSitter;

/// <summary>
/// Tree-sitter S-expression queries for extracting C# structural elements.
/// Purpose: Centralize all queries used by <see cref="CSharpTreeSitterClient"/>.
/// Complexity: One constant per construct group. Combined query concatenates all patterns
/// for single-pass extraction. Pattern indices are positional — see <see cref="ClassifyPattern"/>.
/// </summary>
public static class CSharpQueries
{
    public const string UsingDirectives = """
        ;; using System; / using static System.Math; / global using System; / using Alias = Type;
        (using_directive) @using_decl
        """;

    public const string NamespaceDeclarations = """
        ;; namespace Foo.Bar { ... }
        (namespace_declaration
            name: (_) @namespace_name) @namespace_decl

        ;; namespace Foo.Bar;
        (file_scoped_namespace_declaration
            name: (_) @namespace_name) @namespace_decl
        """;

    public const string ClassDeclarations = """
        ;; class Foo : Base, IBar { ... }
        (class_declaration
            name: (identifier) @class_name) @class_decl
        """;

    public const string StructDeclarations = """
        ;; struct Foo { ... }
        (struct_declaration
            name: (identifier) @struct_name) @struct_decl
        """;

    public const string RecordDeclarations = """
        ;; record Foo(...) / record class Foo / record struct Foo
        (record_declaration
            name: (identifier) @record_name) @record_decl
        """;

    public const string InterfaceDeclarations = """
        ;; interface IFoo { ... }
        (interface_declaration
            name: (identifier) @interface_name) @interface_decl
        """;

    public const string EnumDeclarations = """
        ;; enum Foo { A, B, C }
        (enum_declaration
            name: (identifier) @enum_name) @enum_decl
        """;

    public const string MethodDeclarations = """
        ;; public async Task<int> Foo(string bar) { ... }
        (method_declaration
            returns: (_) @method_return
            name: (identifier) @method_name
            parameters: (parameter_list) @method_params) @method_decl
        """;

    public const string ConstructorDeclarations = """
        ;; public Foo(int x) : base(x) { ... }
        (constructor_declaration
            name: (identifier) @ctor_name
            parameters: (parameter_list) @ctor_params) @ctor_decl
        """;

    public const string PropertyDeclarations = """
        ;; public string Name { get; set; }
        (property_declaration
            type: (_) @prop_type
            name: (identifier) @prop_name) @prop_decl
        """;

    public const string FieldDeclarations = """
        ;; private readonly string _name = "default";
        (field_declaration) @field_decl
        """;

    public const string EventDeclarations = """
        ;; event EventHandler<T> Changed { add { } remove { } }
        (event_declaration
            type: (_) @event_type
            name: (identifier) @event_name) @event_decl

        ;; event EventHandler Changed;
        (event_field_declaration) @event_field_decl
        """;

    public const string IndexerDeclarations = """
        ;; public string this[int index] { get; }
        (indexer_declaration
            type: (_) @indexer_type) @indexer_decl
        """;

    public const string Comments = """
        ;; // comment, /* comment */, /// <summary>doc</summary>
        (comment) @comment
        """;

    /// <summary>
    /// All 16 patterns concatenated in canonical order.
    /// Compiled once into a static <see cref="TreeSitter.Query"/> for single-pass extraction.
    /// Pattern indices are positional — see <see cref="ClassifyPattern"/>.
    /// </summary>
    public static readonly string CombinedQuery = string.Join("\n\n",
        UsingDirectives,         // pattern  0        (1 pattern)
        NamespaceDeclarations,   // patterns 1-2      (2 patterns)
        ClassDeclarations,       // pattern  3        (1 pattern)
        StructDeclarations,      // pattern  4        (1 pattern)
        RecordDeclarations,      // pattern  5        (1 pattern)
        InterfaceDeclarations,   // pattern  6        (1 pattern)
        EnumDeclarations,        // pattern  7        (1 pattern)
        MethodDeclarations,      // pattern  8        (1 pattern)
        ConstructorDeclarations, // pattern  9        (1 pattern)
        PropertyDeclarations,    // pattern  10       (1 pattern)
        FieldDeclarations,       // pattern  11       (1 pattern)
        EventDeclarations,       // patterns 12-13    (2 patterns)
        IndexerDeclarations,     // pattern  14       (1 pattern)
        Comments);               // pattern  15       (1 pattern)

    public static CSharpPatternGroup ClassifyPattern(int patternIndex) => patternIndex switch
    {
        0 => CSharpPatternGroup.UsingDirectives,
        1 or 2 => CSharpPatternGroup.NamespaceDeclarations,
        3 => CSharpPatternGroup.ClassDeclarations,
        4 => CSharpPatternGroup.StructDeclarations,
        5 => CSharpPatternGroup.RecordDeclarations,
        6 => CSharpPatternGroup.InterfaceDeclarations,
        7 => CSharpPatternGroup.EnumDeclarations,
        8 => CSharpPatternGroup.MethodDeclarations,
        9 => CSharpPatternGroup.ConstructorDeclarations,
        10 => CSharpPatternGroup.PropertyDeclarations,
        11 => CSharpPatternGroup.FieldDeclarations,
        12 or 13 => CSharpPatternGroup.EventDeclarations,
        14 => CSharpPatternGroup.IndexerDeclarations,
        15 => CSharpPatternGroup.Comments,
        _ => throw new ArgumentOutOfRangeException(nameof(patternIndex), patternIndex,
            $"Unknown C# query pattern index {patternIndex}. Combined query has 16 patterns (0-15).")
    };
}
