namespace RepoQL.Formats.Rust.TreeSitter;

internal static class RustQueries
{
    public const string StructDeclarations = """
        ;; Structs
        (struct_item name: (type_identifier) @name
            type_parameters: (type_parameters)? @generics
            body: (field_declaration_list)? @body) @struct
        """;

    public const string EnumDeclarations = """
        ;; Enums
        (enum_item name: (type_identifier) @name
            type_parameters: (type_parameters)? @generics
            body: (enum_variant_list) @body) @enum
        """;

    public const string EnumVariants = """
        ;; Enum variants
        (enum_variant name: (identifier) @name) @variant
        """;

    public const string TraitDeclarations = """
        ;; Traits
        (trait_item name: (type_identifier) @name
            type_parameters: (type_parameters)? @generics
            bounds: (trait_bounds)? @supertraits
            body: (declaration_list) @body) @trait
        """;

    public const string ImplBlocks = """
        ;; Impl blocks (inherent and trait)
        (impl_item
            trait: (_)? @trait_name
            type: (_) @target_type
            body: (declaration_list) @body) @impl
        """;

    public const string FunctionDeclarations = """
        ;; Functions (free and method)
        (function_item name: (identifier) @name
            parameters: (parameters) @params
            return_type: (_)? @return_type) @function
        """;

    public const string FunctionSignatures = """
        ;; Function signatures (trait method declarations without body)
        (function_signature_item name: (identifier) @name
            parameters: (parameters) @params
            return_type: (_)? @return_type) @function_sig
        """;

    public const string ModuleDeclarations = """
        ;; Modules
        (mod_item name: (identifier) @name
            body: (declaration_list)? @body) @module
        """;

    public const string UseDeclarations = """
        ;; Use declarations
        (use_declaration argument: (_) @path) @use
        """;

    public const string Constants = """
        ;; Constants
        (const_item name: (identifier) @name
            type: (_) @const_type) @const
        """;

    public const string Statics = """
        ;; Statics
        (static_item name: (identifier) @name
            type: (_) @static_type) @static
        """;

    public const string TypeAliases = """
        ;; Type aliases
        (type_item name: (type_identifier) @name
            type: (_) @aliased_type) @type_alias
        """;

    public const string UnionDefinitions = """
        ;; Union definitions
        (union_item name: (type_identifier) @name
            body: (field_declaration_list) @body) @union
        """;

    public const string MacroDefinitions = """
        ;; Macro definitions
        (macro_definition name: (identifier) @name) @macro_def
        """;

    public const string MacroInvocations = """
        ;; Macro invocations
        (macro_invocation macro: (_) @macro_name) @macro_call
        """;

    public const string Attributes = """
        ;; Attributes (including derive)
        (attribute_item (attribute
            (_) @attr_name
            arguments: (token_tree)? @attr_args)) @attribute
        """;

    public const string VisibilityModifiers = """
        ;; Visibility modifiers
        (visibility_modifier) @visibility
        """;

    public const string ExternBlocks = """
        ;; Extern blocks
        (foreign_mod_item) @extern_block
        """;

    public const string StructFields = """
        ;; Struct fields
        (field_declaration name: (field_identifier) @name
            type: (_) @field_type) @field
        """;

    public const string AssociatedTypes = """
        ;; Associated types in traits
        (associated_type name: (type_identifier) @name) @assoc_type
        """;

    public const string AssociatedConsts = """
        ;; Associated consts in traits and impls
        (const_item name: (identifier) @name
            type: (_) @const_type) @assoc_const
        """;

    public const string DocComments = """
        ;; Doc comments (/// and //! and /** */ and /*! */)
        [
          (line_comment)
          (block_comment)
        ] @doc_comment
        """;

    /// <summary>
    /// All 17 patterns concatenated in canonical order.
    /// Compiled once into a static <see cref="TreeSitter.Query"/> for single-pass extraction.
    /// Pattern indices are positional — see <see cref="ClassifyPattern"/>.
    /// </summary>
    public static readonly string CombinedQuery = string.Join("\n\n",
        StructDeclarations,     // pattern  0        (1 pattern)
        EnumDeclarations,       // pattern  1        (1 pattern)
        TraitDeclarations,      // pattern  2        (1 pattern)
        ImplBlocks,             // pattern  3        (1 pattern)
        FunctionDeclarations,   // pattern  4        (1 pattern)
        FunctionSignatures,     // pattern  5        (1 pattern)
        ModuleDeclarations,     // pattern  6        (1 pattern)
        UseDeclarations,        // pattern  7        (1 pattern)
        Constants,              // pattern  8        (1 pattern)
        Statics,                // pattern  9        (1 pattern)
        TypeAliases,            // pattern 10        (1 pattern)
        UnionDefinitions,       // pattern 11        (1 pattern)
        MacroDefinitions,       // pattern 12        (1 pattern)
        MacroInvocations,       // pattern 13        (1 pattern)
        Attributes,             // pattern 14        (1 pattern)
        VisibilityModifiers,    // pattern 15        (1 pattern)
        ExternBlocks);          // pattern 16        (1 pattern)

    public static RustPatternGroup ClassifyPattern(int patternIndex) => patternIndex switch
    {
        0 => RustPatternGroup.StructDeclarations,
        1 => RustPatternGroup.EnumDeclarations,
        2 => RustPatternGroup.TraitDeclarations,
        3 => RustPatternGroup.ImplBlocks,
        4 => RustPatternGroup.FunctionDeclarations,
        5 => RustPatternGroup.FunctionSignatures,
        6 => RustPatternGroup.ModuleDeclarations,
        7 => RustPatternGroup.UseDeclarations,
        8 => RustPatternGroup.Constants,
        9 => RustPatternGroup.Statics,
        10 => RustPatternGroup.TypeAliases,
        11 => RustPatternGroup.UnionDefinitions,
        12 => RustPatternGroup.MacroDefinitions,
        13 => RustPatternGroup.MacroInvocations,
        14 => RustPatternGroup.Attributes,
        15 => RustPatternGroup.VisibilityModifiers,
        16 => RustPatternGroup.ExternBlocks,
        _ => throw new ArgumentOutOfRangeException(nameof(patternIndex), patternIndex,
            $"Unknown Rust query pattern index {patternIndex}. Combined query has 17 patterns (0-16).")
    };
}
