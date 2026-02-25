using System.Text;
using System.Text.RegularExpressions;
using RepoQL.Formats.Rust.Surface;
using TreeSitter;

namespace RepoQL.Formats.Rust.TreeSitter;

internal sealed partial class RustTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public RustDocumentSurface Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new RustDocumentSurface(
                Structs: [],
                Enums: [],
                Traits: [],
                ImplBlocks: [],
                Functions: [],
                Modules: [],
                Constants: [],
                Statics: [],
                TypeAliases: [],
                Unions: [],
                MacroDefs: [],
                MacroInvocations: [],
                UseDeclarations: [],
                Attributes: [],
                ExternBlocks: [],
                Stats: new RustParseStats(0, 0, 0, 0, 0, 0),
                ErrorNodeCount: 0);
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;
            var source = new SourceContext(sourceCode);

            var errorNodeCount = CountErrorNodes(root);
            var visibilityByNode = BuildVisibilityLookup(root, source);
            var (attributes, attributesByNode) = ExtractAttributes(root, source);

            var structs = ExtractStructs(root, source, visibilityByNode, attributesByNode);
            var enums = ExtractEnums(root, source, visibilityByNode, attributesByNode);
            var traits = ExtractTraits(root, source, visibilityByNode);
            var implBlocks = ExtractImplBlocks(root, source, visibilityByNode);
            var functions = ExtractFunctions(root, source, visibilityByNode, attributesByNode);
            var modules = ExtractModules(root, source, visibilityByNode);
            var constants = ExtractConstants(root, source, visibilityByNode);
            var statics = ExtractStatics(root, source, visibilityByNode);
            var typeAliases = ExtractTypeAliases(root, source, visibilityByNode);
            var unions = ExtractUnions(root, source, visibilityByNode, attributesByNode);
            var macroDefs = ExtractMacroDefs(root, source, visibilityByNode);
            var macroInvocations = ExtractMacroInvocations(root, source);
            var useDeclarations = ExtractUseDeclarations(root, source, visibilityByNode);
            var externBlocks = ExtractExternBlocks(root, source);

            return new RustDocumentSurface(
                Structs: structs,
                Enums: enums,
                Traits: traits,
                ImplBlocks: implBlocks,
                Functions: functions,
                Modules: modules,
                Constants: constants,
                Statics: statics,
                TypeAliases: typeAliases,
                Unions: unions,
                MacroDefs: macroDefs,
                MacroInvocations: macroInvocations,
                UseDeclarations: useDeclarations,
                Attributes: attributes,
                ExternBlocks: externBlocks,
                Stats: new RustParseStats(
                    StructCount: structs.Count,
                    EnumCount: enums.Count,
                    TraitCount: traits.Count,
                    ImplCount: implBlocks.Count,
                    FunctionCount: functions.Count,
                    LineCount: CountLines(sourceCode)),
                ErrorNodeCount: errorNodeCount);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native Rust parser. Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    public IReadOnlyList<RustQueryCaptureGroup> ExecuteQuery(string sourceCode, string query)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(sourceCode))
        {
            return [];
        }

        var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
        using var tree = parser.Parse(sourceCode);
        return ExecuteQuery(query, tree.RootNode, new SourceContext(sourceCode));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var parser in _parsers.Values)
        {
            parser.Dispose();
        }

        _parsers.Dispose();
        _disposed = true;
    }

    private static IReadOnlyList<RustQueryCaptureGroup> ExecuteQuery(string query, Node rootNode, SourceContext source)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        var groups = new List<RustQueryCaptureGroup>();

        foreach (var match in cursor.Matches)
        {
            var captures = match.Captures
                .Where(c => !c.Node.IsError)
                .Select(c =>
                {
                    var range = new RustByteRange(c.Node.StartIndex, c.Node.EndIndex);
                    return new RustQueryCapture(c.Name, source.GetText(range), range);
                })
                .Where(c => !string.IsNullOrEmpty(c.Text))
                .ToList();

            if (captures.Count == 0)
            {
                continue;
            }

            groups.Add(new RustQueryCaptureGroup(match.PatternIndex, captures));
        }

        return groups;
    }

    private static IReadOnlyList<RustStructInfo> ExtractStructs(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode,
        IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> attributesByNode)
    {
        var results = new List<RustStructInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.StructDeclarations, root))
        {
            var structNode = GetCapture(match, "struct");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(structNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var genericsNode = GetCapture(match, "generics");
            var name = NormalizeName(source.GetText(nameNode));
            var attributes = GetNodeAttributes(structNode, source, attributesByNode);

            results.Add(new RustStructInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(structNode, name, source),
                Visibility: GetVisibility(structNode, visibilityByNode),
                Generics: GetOptionalText(genericsNode, source),
                WhereClause: GetWhereClause(structNode, source),
                Derives: ExtractDerives(attributes),
                Attributes: attributes,
                DocComment: ExtractDocComment(structNode, source),
                ByteRange: new RustByteRange(structNode.StartIndex, structNode.EndIndex),
                Fields: IsNullNode(bodyNode)
                    ? []
                    : ExtractFields(bodyNode, source, visibilityByNode)));
        }

        return results
            .OrderBy(s => s.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustEnumInfo> ExtractEnums(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode,
        IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> attributesByNode)
    {
        var results = new List<RustEnumInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.EnumDeclarations, root))
        {
            var enumNode = GetCapture(match, "enum");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(enumNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var genericsNode = GetCapture(match, "generics");
            var name = NormalizeName(source.GetText(nameNode));
            var attributes = GetNodeAttributes(enumNode, source, attributesByNode);

            results.Add(new RustEnumInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(enumNode, name, source),
                Visibility: GetVisibility(enumNode, visibilityByNode),
                Generics: GetOptionalText(genericsNode, source),
                WhereClause: GetWhereClause(enumNode, source),
                Derives: ExtractDerives(attributes),
                Attributes: attributes,
                DocComment: ExtractDocComment(enumNode, source),
                ByteRange: new RustByteRange(enumNode.StartIndex, enumNode.EndIndex),
                Variants: IsNullNode(bodyNode)
                    ? []
                    : ExtractEnumVariants(bodyNode, source, visibilityByNode)));
        }

        return results
            .OrderBy(e => e.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustTraitInfo> ExtractTraits(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustTraitInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.TraitDeclarations, root))
        {
            var traitNode = GetCapture(match, "trait");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(traitNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var genericsNode = GetCapture(match, "generics");
            var supertraitsNode = GetCapture(match, "supertraits");
            var name = NormalizeName(source.GetText(nameNode));
            var traitText = source.GetText(traitNode);

            results.Add(new RustTraitInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(traitNode, name, source),
                Visibility: GetVisibility(traitNode, visibilityByNode),
                Generics: GetOptionalText(genericsNode, source),
                WhereClause: GetWhereClause(traitNode, source),
                Supertraits: GetOptionalText(supertraitsNode, source),
                IsAuto: Regex.IsMatch(traitText, @"\bauto\s+trait\b", RegexOptions.CultureInvariant),
                IsUnsafe: Regex.IsMatch(traitText, @"\bunsafe\s+trait\b", RegexOptions.CultureInvariant),
                DocComment: ExtractDocComment(traitNode, source),
                ByteRange: new RustByteRange(traitNode.StartIndex, traitNode.EndIndex),
                Methods: IsNullNode(bodyNode)
                    ? []
                    : ExtractMethodsFromBody(bodyNode, traitNode, source, visibilityByNode, defaultVisibility: "private"),
                AssociatedTypes: IsNullNode(bodyNode)
                    ? []
                    : ExtractAssociatedTypes(bodyNode, traitNode, source),
                AssociatedConsts: IsNullNode(bodyNode)
                    ? []
                    : ExtractAssociatedConsts(bodyNode, traitNode, source)));
        }

        return results
            .OrderBy(t => t.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustImplBlockInfo> ExtractImplBlocks(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustImplBlockInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.ImplBlocks, root))
        {
            var implNode = GetCapture(match, "impl");
            var targetTypeNode = GetCapture(match, "target_type");
            if (IsNullNode(implNode) || IsNullNode(targetTypeNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var traitNameNode = GetCapture(match, "trait_name");

            results.Add(new RustImplBlockInfo(
                TargetType: NormalizeImplTargetType(targetTypeNode, source),
                TraitName: NormalizeImplTraitName(traitNameNode, source),
                Generics: GetOptionalFieldText(implNode, "type_parameters", source),
                WhereClause: GetWhereClause(implNode, source),
                IsUnsafe: Regex.IsMatch(source.GetText(implNode), @"\bunsafe\s+impl\b", RegexOptions.CultureInvariant),
                ByteRange: new RustByteRange(implNode.StartIndex, implNode.EndIndex),
                Methods: IsNullNode(bodyNode)
                    ? []
                    : ExtractMethodsFromBody(bodyNode, implNode, source, visibilityByNode, defaultVisibility: "private"),
                AssociatedTypes: IsNullNode(bodyNode)
                    ? []
                    : ExtractAssociatedTypes(bodyNode, implNode, source),
                AssociatedConsts: IsNullNode(bodyNode)
                    ? []
                    : ExtractAssociatedConsts(bodyNode, implNode, source)));
        }

        return results
            .OrderBy(i => i.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustFunctionInfo> ExtractFunctions(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode,
        IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> attributesByNode)
    {
        var results = new List<RustFunctionInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.FunctionDeclarations, root))
        {
            var functionNode = GetCapture(match, "function");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(functionNode) || IsNullNode(nameNode))
            {
                continue;
            }

            if (HasAncestor(functionNode, "trait_item")
                || HasAncestor(functionNode, "impl_item")
                || HasAncestor(functionNode, "foreign_mod_item")
                || HasAncestor(functionNode, "macro_invocation")
                || HasAncestor(functionNode, "token_tree"))
            {
                continue;
            }

            var paramsNode = GetCapture(match, "params");
            var returnTypeNode = GetCapture(match, "return_type");
            var attributes = GetNodeAttributes(functionNode, source, attributesByNode);
            var functionText = source.GetText(functionNode);
            var name = NormalizeName(source.GetText(nameNode));

            // Skip phantom function nodes produced by tree-sitter error recovery inside macro bodies
            if (!IsValidRustIdentifier(name))
            {
                continue;
            }

            results.Add(new RustFunctionInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(functionNode, name, source),
                Visibility: GetVisibility(functionNode, visibilityByNode),
                IsAsync: Regex.IsMatch(functionText, @"\basync\b", RegexOptions.CultureInvariant),
                IsUnsafe: Regex.IsMatch(functionText, @"\bunsafe\b", RegexOptions.CultureInvariant),
                IsConst: Regex.IsMatch(functionText, @"\bconst\b", RegexOptions.CultureInvariant),
                Generics: GetOptionalFieldText(functionNode, "type_parameters", source),
                Parameters: GetOptionalText(paramsNode, source),
                ReturnType: NormalizeReturnType(GetOptionalText(returnTypeNode, source)),
                IsTest: HasAttribute(attributes, "test"),
                DocComment: ExtractDocComment(functionNode, source),
                ByteRange: new RustByteRange(functionNode.StartIndex, functionNode.EndIndex)));
        }

        return results
            .OrderBy(f => f.ByteRange.StartByte)
            .ToList();
    }
    private static IReadOnlyList<RustModuleInfo> ExtractModules(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustModuleInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.ModuleDeclarations, root))
        {
            var moduleNode = GetCapture(match, "module");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(moduleNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var name = NormalizeName(source.GetText(nameNode));

            results.Add(new RustModuleInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(moduleNode, name, source),
                Visibility: GetVisibility(moduleNode, visibilityByNode),
                IsInline: !IsNullNode(bodyNode),
                DocComment: ExtractDocComment(moduleNode, source),
                ByteRange: new RustByteRange(moduleNode.StartIndex, moduleNode.EndIndex)));
        }

        return results
            .OrderBy(m => m.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustConstantInfo> ExtractConstants(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustConstantInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.Constants, root))
        {
            var constNode = GetCapture(match, "const");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(constNode) || IsNullNode(nameNode))
            {
                continue;
            }

            if (HasAncestor(constNode, "trait_item") || HasAncestor(constNode, "impl_item"))
            {
                continue;
            }

            var constTypeNode = GetCapture(match, "const_type");
            results.Add(new RustConstantInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Visibility: GetVisibility(constNode, visibilityByNode),
                ConstType: GetOptionalText(constTypeNode, source),
                DocComment: ExtractDocComment(constNode, source),
                ByteRange: new RustByteRange(constNode.StartIndex, constNode.EndIndex)));
        }

        return results
            .OrderBy(c => c.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustStaticInfo> ExtractStatics(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustStaticInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.Statics, root))
        {
            var staticNode = GetCapture(match, "static");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(staticNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var staticTypeNode = GetCapture(match, "static_type");
            var staticText = source.GetText(staticNode);

            results.Add(new RustStaticInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Visibility: GetVisibility(staticNode, visibilityByNode),
                StaticType: GetOptionalText(staticTypeNode, source),
                IsMutable: Regex.IsMatch(staticText, @"\bstatic\s+mut\b", RegexOptions.CultureInvariant),
                DocComment: ExtractDocComment(staticNode, source),
                ByteRange: new RustByteRange(staticNode.StartIndex, staticNode.EndIndex)));
        }

        return results
            .OrderBy(s => s.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustTypeAliasInfo> ExtractTypeAliases(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustTypeAliasInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.TypeAliases, root))
        {
            var aliasNode = GetCapture(match, "type_alias");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(aliasNode) || IsNullNode(nameNode))
            {
                continue;
            }

            if (HasAncestor(aliasNode, "trait_item") || HasAncestor(aliasNode, "impl_item"))
            {
                continue;
            }

            var aliasedTypeNode = GetCapture(match, "aliased_type");
            var name = NormalizeName(source.GetText(nameNode));

            results.Add(new RustTypeAliasInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(aliasNode, name, source),
                Visibility: GetVisibility(aliasNode, visibilityByNode),
                Generics: GetOptionalFieldText(aliasNode, "type_parameters", source),
                AliasedType: GetOptionalText(aliasedTypeNode, source),
                ByteRange: new RustByteRange(aliasNode.StartIndex, aliasNode.EndIndex)));
        }

        return results
            .OrderBy(t => t.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustUnionInfo> ExtractUnions(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode,
        IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> attributesByNode)
    {
        var results = new List<RustUnionInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.UnionDefinitions, root))
        {
            var unionNode = GetCapture(match, "union");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(unionNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var bodyNode = GetCapture(match, "body");
            var name = NormalizeName(source.GetText(nameNode));
            var attributes = GetNodeAttributes(unionNode, source, attributesByNode);

            results.Add(new RustUnionInfo(
                Name: name,
                QualifiedName: BuildQualifiedName(unionNode, name, source),
                Visibility: GetVisibility(unionNode, visibilityByNode),
                Generics: GetOptionalFieldText(unionNode, "type_parameters", source),
                Derives: ExtractDerives(attributes),
                Attributes: attributes,
                DocComment: ExtractDocComment(unionNode, source),
                ByteRange: new RustByteRange(unionNode.StartIndex, unionNode.EndIndex),
                Fields: IsNullNode(bodyNode)
                    ? []
                    : ExtractFields(bodyNode, source, visibilityByNode)));
        }

        return results
            .OrderBy(u => u.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustMacroDefInfo> ExtractMacroDefs(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustMacroDefInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.MacroDefinitions, root))
        {
            var macroNode = GetCapture(match, "macro_def");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(macroNode) || IsNullNode(nameNode))
            {
                continue;
            }

            results.Add(new RustMacroDefInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Visibility: GetVisibility(macroNode, visibilityByNode),
                ByteRange: new RustByteRange(macroNode.StartIndex, macroNode.EndIndex)));
        }

        return results
            .OrderBy(m => m.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustMacroInvocationInfo> ExtractMacroInvocations(Node root, SourceContext source)
    {
        var results = new List<RustMacroInvocationInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.MacroInvocations, root))
        {
            var callNode = GetCapture(match, "macro_call");
            var macroNameNode = GetCapture(match, "macro_name");
            if (IsNullNode(callNode) || IsNullNode(macroNameNode))
            {
                continue;
            }

            results.Add(new RustMacroInvocationInfo(
                MacroName: NormalizeName(source.GetText(macroNameNode)),
                ByteRange: new RustByteRange(callNode.StartIndex, callNode.EndIndex)));
        }

        return results
            .OrderBy(m => m.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustUseDeclarationInfo> ExtractUseDeclarations(
        Node root,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var results = new List<RustUseDeclarationInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.UseDeclarations, root))
        {
            var useNode = GetCapture(match, "use");
            var pathNode = GetCapture(match, "path");
            if (IsNullNode(useNode) || IsNullNode(pathNode))
            {
                continue;
            }

            var path = NormalizeWhitespace(source.GetText(pathNode));
            var useText = source.GetText(useNode);

            results.Add(new RustUseDeclarationInfo(
                Path: path,
                Alias: ExtractUseAlias(useText),
                IsGlob: path.Contains('*', StringComparison.Ordinal),
                IsPub: !string.Equals(GetVisibility(useNode, visibilityByNode), "private", StringComparison.Ordinal),
                ByteRange: new RustByteRange(useNode.StartIndex, useNode.EndIndex)));
        }

        return results
            .OrderBy(u => u.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustExternBlockInfo> ExtractExternBlocks(Node root, SourceContext source)
    {
        var results = new List<RustExternBlockInfo>();
        foreach (var capture in ExecuteCapturesSafe(RustQueries.ExternBlocks, root).Where(c => c.Name == "extern_block"))
        {
            var externNode = capture.Node;
            if (IsNullNode(externNode))
            {
                continue;
            }

            var externText = source.GetText(externNode);
            var abiMatch = Regex.Match(externText, "\\bextern\\s+\"(?<abi>[^\"]+)\"", RegexOptions.CultureInvariant);

            results.Add(new RustExternBlockInfo(
                Abi: abiMatch.Success ? abiMatch.Groups["abi"].Value : null,
                ByteRange: new RustByteRange(externNode.StartIndex, externNode.EndIndex),
                Functions: ExtractExternFunctions(externNode, source)));
        }

        return results
            .OrderBy(e => e.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustExternFunctionInfo> ExtractExternFunctions(Node externNode, SourceContext source)
    {
        var results = new List<RustExternFunctionInfo>();

        foreach (var match in ExecuteMatchesSafe(RustQueries.FunctionSignatures, externNode))
        {
            var signatureNode = GetCapture(match, "function_sig");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(signatureNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var paramsNode = GetCapture(match, "params");
            var returnNode = GetCapture(match, "return_type");

            results.Add(new RustExternFunctionInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Parameters: GetOptionalText(paramsNode, source),
                ReturnType: NormalizeReturnType(GetOptionalText(returnNode, source)),
                ByteRange: new RustByteRange(signatureNode.StartIndex, signatureNode.EndIndex)));
        }

        foreach (var match in ExecuteMatchesSafe(RustQueries.FunctionDeclarations, externNode))
        {
            var functionNode = GetCapture(match, "function");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(functionNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var paramsNode = GetCapture(match, "params");
            var returnNode = GetCapture(match, "return_type");

            results.Add(new RustExternFunctionInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Parameters: GetOptionalText(paramsNode, source),
                ReturnType: NormalizeReturnType(GetOptionalText(returnNode, source)),
                ByteRange: new RustByteRange(functionNode.StartIndex, functionNode.EndIndex)));
        }

        return results
            .GroupBy(f => $"{f.ByteRange.StartByte}:{f.ByteRange.EndByte}:{f.Name}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustMethodInfo> ExtractMethodsFromBody(
        Node bodyNode,
        Node ownerNode,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode,
        string defaultVisibility)
    {
        var methods = new List<RustMethodInfo>();

        foreach (var match in ExecuteMatchesSafe(RustQueries.FunctionDeclarations, bodyNode))
        {
            var methodNode = GetCapture(match, "function");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(methodNode) || IsNullNode(nameNode) || !IsDirectMemberOfOwner(methodNode, ownerNode))
            {
                continue;
            }

            var paramsNode = GetCapture(match, "params");
            var returnTypeNode = GetCapture(match, "return_type");
            var methodText = source.GetText(methodNode);
            var parameters = GetOptionalText(paramsNode, source);
            var methodName = NormalizeName(source.GetText(nameNode));

            // Skip phantom function nodes produced by tree-sitter error recovery inside macro bodies
            if (!IsValidRustIdentifier(methodName))
            {
                continue;
            }

            methods.Add(new RustMethodInfo(
                Name: methodName,
                Visibility: GetVisibility(methodNode, visibilityByNode, defaultVisibility),
                IsAsync: Regex.IsMatch(methodText, @"\basync\b", RegexOptions.CultureInvariant),
                IsUnsafe: Regex.IsMatch(methodText, @"\bunsafe\b", RegexOptions.CultureInvariant),
                IsConst: Regex.IsMatch(methodText, @"\bconst\b", RegexOptions.CultureInvariant),
                SelfKind: DetermineSelfKind(parameters),
                Parameters: parameters,
                ReturnType: NormalizeReturnType(GetOptionalText(returnTypeNode, source)),
                HasDefault: methodText.Contains('{', StringComparison.Ordinal),
                DocComment: ExtractDocComment(methodNode, source),
                ByteRange: new RustByteRange(methodNode.StartIndex, methodNode.EndIndex)));
        }

        foreach (var match in ExecuteMatchesSafe(RustQueries.FunctionSignatures, bodyNode))
        {
            var methodNode = GetCapture(match, "function_sig");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(methodNode) || IsNullNode(nameNode) || !IsDirectMemberOfOwner(methodNode, ownerNode))
            {
                continue;
            }

            var paramsNode = GetCapture(match, "params");
            var returnTypeNode = GetCapture(match, "return_type");
            var methodText = source.GetText(methodNode);
            var parameters = GetOptionalText(paramsNode, source);
            var sigName = NormalizeName(source.GetText(nameNode));

            // Skip phantom function nodes produced by tree-sitter error recovery inside macro bodies
            if (!IsValidRustIdentifier(sigName))
            {
                continue;
            }

            methods.Add(new RustMethodInfo(
                Name: sigName,
                Visibility: GetVisibility(methodNode, visibilityByNode, defaultVisibility),
                IsAsync: Regex.IsMatch(methodText, @"\basync\b", RegexOptions.CultureInvariant),
                IsUnsafe: Regex.IsMatch(methodText, @"\bunsafe\b", RegexOptions.CultureInvariant),
                IsConst: Regex.IsMatch(methodText, @"\bconst\b", RegexOptions.CultureInvariant),
                SelfKind: DetermineSelfKind(parameters),
                Parameters: parameters,
                ReturnType: NormalizeReturnType(GetOptionalText(returnTypeNode, source)),
                HasDefault: methodText.Contains('{', StringComparison.Ordinal),
                DocComment: ExtractDocComment(methodNode, source),
                ByteRange: new RustByteRange(methodNode.StartIndex, methodNode.EndIndex)));
        }

        return methods
            .GroupBy(m => $"{m.ByteRange.StartByte}:{m.ByteRange.EndByte}:{m.Name}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustAssociatedTypeInfo> ExtractAssociatedTypes(Node bodyNode, Node ownerNode, SourceContext source)
    {
        var results = new List<RustAssociatedTypeInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.AssociatedTypes, bodyNode))
        {
            var assocNode = GetCapture(match, "assoc_type");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(assocNode) || IsNullNode(nameNode) || !IsDirectMemberOfOwner(assocNode, ownerNode))
            {
                continue;
            }

            var text = source.GetText(assocNode);
            var bounds = Regex.Match(text, @":\s*(?<bounds>[^=;]+)", RegexOptions.CultureInvariant).Groups["bounds"].Value;
            var defaultType = Regex.Match(text, @"=\s*(?<default>[^;]+)", RegexOptions.CultureInvariant).Groups["default"].Value;

            results.Add(new RustAssociatedTypeInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                Bounds: string.IsNullOrWhiteSpace(bounds) ? null : NormalizeWhitespace(bounds),
                DefaultType: string.IsNullOrWhiteSpace(defaultType) ? null : NormalizeWhitespace(defaultType),
                ByteRange: new RustByteRange(assocNode.StartIndex, assocNode.EndIndex)));
        }

        if (ownerNode.Type == "impl_item")
        {
            foreach (var match in ExecuteMatchesSafe(RustQueries.TypeAliases, bodyNode))
            {
                var typeAliasNode = GetCapture(match, "type_alias");
                var nameNode = GetCapture(match, "name");
                if (IsNullNode(typeAliasNode) || IsNullNode(nameNode) || !IsDirectMemberOfOwner(typeAliasNode, ownerNode))
                {
                    continue;
                }

                var defaultTypeNode = GetCapture(match, "aliased_type");
                results.Add(new RustAssociatedTypeInfo(
                    Name: NormalizeName(source.GetText(nameNode)),
                    Bounds: null,
                    DefaultType: GetOptionalText(defaultTypeNode, source),
                    ByteRange: new RustByteRange(typeAliasNode.StartIndex, typeAliasNode.EndIndex)));
            }
        }

        return results
            .GroupBy(t => $"{t.ByteRange.StartByte}:{t.ByteRange.EndByte}:{t.Name}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustAssociatedConstInfo> ExtractAssociatedConsts(Node bodyNode, Node ownerNode, SourceContext source)
    {
        var results = new List<RustAssociatedConstInfo>();
        foreach (var match in ExecuteMatchesSafe(RustQueries.AssociatedConsts, bodyNode))
        {
            var assocNode = GetCapture(match, "assoc_const");
            var nameNode = GetCapture(match, "name");
            if (IsNullNode(assocNode) || IsNullNode(nameNode) || !IsDirectMemberOfOwner(assocNode, ownerNode))
            {
                continue;
            }

            var constTypeNode = GetCapture(match, "const_type");
            var text = source.GetText(assocNode);
            var fallbackType = Regex.Match(text, @":\s*(?<type>[^=;]+)", RegexOptions.CultureInvariant).Groups["type"].Value;
            var constType = GetOptionalText(constTypeNode, source);
            if (string.IsNullOrWhiteSpace(constType))
            {
                constType = string.IsNullOrWhiteSpace(fallbackType) ? null : NormalizeWhitespace(fallbackType);
            }

            results.Add(new RustAssociatedConstInfo(
                Name: NormalizeName(source.GetText(nameNode)),
                ConstType: constType,
                HasDefault: text.Contains('=', StringComparison.Ordinal),
                ByteRange: new RustByteRange(assocNode.StartIndex, assocNode.EndIndex)));
        }

        return results
            .OrderBy(c => c.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustFieldInfo> ExtractFields(
        Node bodyNode,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var fields = new List<RustFieldInfo>();

        foreach (var child in bodyNode.NamedChildren)
        {
            if (!string.Equals(child.Type, "field_declaration", StringComparison.Ordinal))
            {
                continue;
            }

            var nameNode = TryGetField(child, "name");
            var typeNode = TryGetField(child, "type");
            var name = IsNullNode(nameNode)
                ? Regex.Match(source.GetText(child), @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.CultureInvariant).Groups["name"].Value
                : NormalizeName(source.GetText(nameNode));

            fields.Add(new RustFieldInfo(
                Name: string.IsNullOrWhiteSpace(name) ? "_" : name,
                Visibility: GetVisibility(child, visibilityByNode),
                FieldType: GetOptionalText(typeNode, source),
                DocComment: ExtractDocComment(child, source),
                ByteRange: new RustByteRange(child.StartIndex, child.EndIndex)));
        }

        return fields
            .OrderBy(f => f.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustEnumVariantInfo> ExtractEnumVariants(
        Node enumBodyNode,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        var variants = new List<RustEnumVariantInfo>();
        foreach (var variantNode in enumBodyNode.NamedChildren.Where(c => c.Type == "enum_variant"))
        {
            var nameNode = TryGetField(variantNode, "name");
            var variantText = source.GetText(variantNode);
            var name = IsNullNode(nameNode)
                ? Regex.Match(variantText, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant).Groups["name"].Value
                : NormalizeName(source.GetText(nameNode));

            var discriminant = Regex.Match(variantText, @"=\s*(?<value>.+)$", RegexOptions.CultureInvariant).Groups["value"].Value;
            discriminant = string.IsNullOrWhiteSpace(discriminant) ? null : NormalizeWhitespace(discriminant);

            var kind = DetermineVariantKind(variantNode, variantText);
            var fields = kind switch
            {
                "struct" => ExtractVariantStructFields(variantNode, source, visibilityByNode),
                "tuple" => ExtractVariantTupleFields(variantNode, variantText),
                _ => []
            };

            variants.Add(new RustEnumVariantInfo(
                Name: string.IsNullOrWhiteSpace(name) ? "_" : name,
                VariantKind: kind,
                Fields: fields,
                Discriminant: discriminant,
                DocComment: ExtractDocComment(variantNode, source),
                ByteRange: new RustByteRange(variantNode.StartIndex, variantNode.EndIndex)));
        }

        return variants
            .OrderBy(v => v.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<RustFieldInfo> ExtractVariantStructFields(
        Node variantNode,
        SourceContext source,
        IReadOnlyDictionary<string, string> visibilityByNode)
    {
        foreach (var child in variantNode.NamedChildren)
        {
            if (child.Type == "field_declaration_list")
            {
                return ExtractFields(child, source, visibilityByNode);
            }
        }

        return [];
    }

    private static IReadOnlyList<RustFieldInfo> ExtractVariantTupleFields(Node variantNode, string variantText)
    {
        var open = variantText.IndexOf('(');
        var close = variantText.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return [];
        }

        var tupleText = variantText[(open + 1)..close];
        var fields = SplitTopLevelCommaSeparated(tupleText)
            .Select((part, index) => new RustFieldInfo(
                Name: $"Item{index + 1}",
                Visibility: "private",
                FieldType: NormalizeWhitespace(part),
                DocComment: null,
                ByteRange: new RustByteRange(variantNode.StartIndex, variantNode.EndIndex)))
            .ToList();

        return fields;
    }

    private static string DetermineVariantKind(Node variantNode, string variantText)
    {
        if (variantNode.NamedChildren.Any(c => c.Type == "field_declaration_list"))
        {
            return "struct";
        }

        var openParen = variantText.IndexOf('(');
        var equals = variantText.IndexOf('=');
        if (openParen >= 0 && (equals < 0 || openParen < equals))
        {
            return "tuple";
        }

        return "unit";
    }

    private static (IReadOnlyList<RustAttributeInfo> Attributes, IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> ByNode)
        ExtractAttributes(Node root, SourceContext source)
    {
        var allAttributes = new List<RustAttributeInfo>();
        var byNode = new Dictionary<string, List<RustAttributeInfo>>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatchesSafe(RustQueries.Attributes, root))
        {
            var attributeNode = GetCapture(match, "attribute");
            var nameNode = GetCapture(match, "attr_name");
            if (IsNullNode(attributeNode))
            {
                continue;
            }

            var argsNode = GetCapture(match, "attr_args");
            var attributeText = source.GetText(attributeNode);
            var attributeName = ExtractAttributeName(attributeText);
            if (string.IsNullOrWhiteSpace(attributeName) && !IsNullNode(nameNode))
            {
                attributeName = NormalizeName(source.GetText(nameNode));
            }

            if (string.IsNullOrWhiteSpace(attributeName))
            {
                continue;
            }

            var attributeArgs = GetOptionalText(argsNode, source);
            if (string.IsNullOrWhiteSpace(attributeArgs))
            {
                attributeArgs = ExtractAttributeArguments(attributeText);
            }

            var attribute = new RustAttributeInfo(
                Name: attributeName,
                Arguments: attributeArgs,
                ByteRange: new RustByteRange(attributeNode.StartIndex, attributeNode.EndIndex));

            allAttributes.Add(attribute);

            var targetNode = FindAttributeTarget(attributeNode);
            if (IsNullNode(targetNode))
            {
                continue;
            }

            var key = GetNodeKey(targetNode);
            if (!byNode.TryGetValue(key, out var list))
            {
                list = [];
                byNode[key] = list;
            }

            list.Add(attribute);
        }

        var deduped = allAttributes
            .GroupBy(a => $"{a.ByteRange.StartByte}:{a.ByteRange.EndByte}:{a.Name}:{a.Arguments}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => a.ByteRange.StartByte)
            .ToList();

        var readOnlyMap = byNode.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<RustAttributeInfo>)kvp.Value
                .OrderBy(a => a.ByteRange.StartByte)
                .ToList(),
            StringComparer.Ordinal);

        return (deduped, readOnlyMap);
    }

    private static Node FindAttributeTarget(Node attributeNode)
    {
        var current = attributeNode.Parent;
        while (!IsNullNode(current))
        {
            if (current.Type == "attribute_item")
            {
                var target = FirstNonAttributeChild(current);
                return UnwrapAttributeItem(target);
            }

            current = current.Parent;
        }

        return default;
    }

    private static Node FirstNonAttributeChild(Node node)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type is "attribute" or "line_comment" or "block_comment")
            {
                continue;
            }

            return child;
        }

        return default;
    }

    private static Node UnwrapAttributeItem(Node node)
    {
        var current = node;
        while (!IsNullNode(current) && current.Type == "attribute_item")
        {
            current = FirstNonAttributeChild(current);
        }

        return current;
    }

    private static IReadOnlyDictionary<string, string> BuildVisibilityLookup(Node root, SourceContext source)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capture in ExecuteCapturesSafe(RustQueries.VisibilityModifiers, root))
        {
            if (capture.Name != "visibility")
            {
                continue;
            }

            var owner = FindVisibilityOwner(capture.Node);
            if (IsNullNode(owner))
            {
                continue;
            }

            map[GetNodeKey(owner)] = NormalizeVisibility(source.GetText(capture.Node));
        }

        return map;
    }

    private static Node FindVisibilityOwner(Node visibilityNode)
    {
        var current = visibilityNode.Parent;
        while (!IsNullNode(current))
        {
            if (current.Type is "struct_item"
                or "enum_item"
                or "trait_item"
                or "impl_item"
                or "function_item"
                or "function_signature_item"
                or "mod_item"
                or "const_item"
                or "static_item"
                or "type_item"
                or "union_item"
                or "macro_definition"
                or "use_declaration"
                or "field_declaration")
            {
                return current;
            }

            current = current.Parent;
        }

        return default;
    }

    private static IReadOnlyList<RustAttributeInfo> GetNodeAttributes(
        Node node,
        SourceContext source,
        IReadOnlyDictionary<string, IReadOnlyList<RustAttributeInfo>> attributesByNode)
    {
        var key = GetNodeKey(node);
        if (attributesByNode.TryGetValue(key, out var attributes))
        {
            return attributes;
        }

        return ExtractLeadingAttributes(node, source);
    }

    private static IReadOnlyList<RustAttributeInfo> ExtractLeadingAttributes(Node node, SourceContext source)
    {
        var lineIndex = source.GetLineIndex(node.StartIndex);
        if (lineIndex <= 0)
        {
            return [];
        }

        var attributes = new List<RustAttributeInfo>();
        for (var i = lineIndex - 1; i >= 0; i--)
        {
            var line = source.Lines[i].Trim();
            if (line.Length == 0)
            {
                if (attributes.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (!line.StartsWith("#[", StringComparison.Ordinal) && !line.StartsWith("#![", StringComparison.Ordinal))
            {
                if (line.StartsWith("///", StringComparison.Ordinal)
                    || line.StartsWith("//!", StringComparison.Ordinal)
                    || line.StartsWith("/**", StringComparison.Ordinal)
                    || line.StartsWith("/*!", StringComparison.Ordinal))
                {
                    continue;
                }

                break;
            }

            if (!TryParseAttributeLine(line, out var name, out var args))
            {
                continue;
            }

            attributes.Add(new RustAttributeInfo(
                Name: name,
                Arguments: args,
                ByteRange: new RustByteRange(source.GetLineStart(i), source.GetLineEndExclusive(i))));
        }

        attributes.Reverse();
        return attributes;
    }

    private static bool TryParseAttributeLine(string line, out string name, out string? args)
    {
        name = string.Empty;
        args = null;

        var match = Regex.Match(line, "^#!?\\[(?<name>[A-Za-z_][A-Za-z0-9_:]*)\\s*(?<args>\\(.*\\))?\\]$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups["name"].Value;
        args = match.Groups["args"].Success
            ? NormalizeWhitespace(match.Groups["args"].Value)
            : null;

        return true;
    }

    private static string? ExtractAttributeName(string attributeText)
    {
        var match = Regex.Match(
            attributeText,
            "^#!?\\[(?<name>[A-Za-z_][A-Za-z0-9_:]*)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string? ExtractAttributeArguments(string attributeText)
    {
        var match = Regex.Match(
            attributeText,
            "^#!?\\[[A-Za-z_][A-Za-z0-9_:]*\\s*(?<args>\\(.*\\))\\s*\\]$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var args = NormalizeWhitespace(match.Groups["args"].Value);
        return string.IsNullOrWhiteSpace(args) ? null : args;
    }

    private static string? ExtractDerives(IReadOnlyList<RustAttributeInfo> attributes)
    {
        var derives = new List<string>();
        foreach (var attribute in attributes.Where(a => string.Equals(a.Name, "derive", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(attribute.Arguments))
            {
                continue;
            }

            var args = attribute.Arguments!.Trim();
            if (args.StartsWith('(') && args.EndsWith(')') && args.Length > 2)
            {
                args = args[1..^1];
            }

            derives.AddRange(SplitTopLevelCommaSeparated(args).Select(NormalizeWhitespace));
        }

        var normalized = derives
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return normalized.Count == 0 ? null : string.Join(", ", normalized);
    }

    private static bool HasAttribute(IReadOnlyList<RustAttributeInfo> attributes, string name)
        => attributes.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private static string GetVisibility(Node node, IReadOnlyDictionary<string, string> visibilityByNode, string defaultValue = "private")
    {
        if (visibilityByNode.TryGetValue(GetNodeKey(node), out var visibility))
        {
            return visibility;
        }

        var visNode = TryGetField(node, "visibility");
        return IsNullNode(visNode)
            ? defaultValue
            : NormalizeVisibility(visNode.Text);
    }

    private static string NormalizeVisibility(string? rawVisibility)
    {
        if (string.IsNullOrWhiteSpace(rawVisibility))
        {
            return "private";
        }

        var normalized = NormalizeWhitespace(rawVisibility!);
        if (string.Equals(normalized, "pub", StringComparison.Ordinal))
        {
            return "public";
        }

        if (normalized.StartsWith("pub(", StringComparison.Ordinal))
        {
            // Check pub(in ...) FIRST — it may contain "crate" or "super" in the path
            if (normalized.StartsWith("pub(in", StringComparison.Ordinal))
            {
                var pathMatch = Regex.Match(normalized, @"pub\(in\s+(?<path>[^)]+)\)", RegexOptions.CultureInvariant);
                return pathMatch.Success ? $"pub_in:{pathMatch.Groups["path"].Value.Trim()}" : normalized;
            }

            if (normalized.Contains("crate", StringComparison.Ordinal))
            {
                return "pub_crate";
            }

            if (normalized.Contains("super", StringComparison.Ordinal))
            {
                return "pub_super";
            }

            return normalized;
        }

        return "private";
    }

    private static string? GetWhereClause(Node node, SourceContext source)
    {
        var whereClause = GetOptionalFieldText(node, "where_clause", source);
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            return whereClause;
        }

        var text = source.GetText(node);
        var match = Regex.Match(text, @"\bwhere\b(?<where>[\s\S]*?)(\{|;|=)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return NormalizeWhitespace($"where {match.Groups["where"].Value}");
    }

    private static string BuildQualifiedName(Node node, string localName, SourceContext source)
    {
        var segments = new Stack<string>();
        segments.Push(localName);

        var current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current.Type == "mod_item")
            {
                var nameNode = TryGetField(current, "name");
                if (!IsNullNode(nameNode))
                {
                    segments.Push(NormalizeName(source.GetText(nameNode)));
                }
            }

            current = current.Parent;
        }

        return string.Join("::", segments);
    }

    private static bool HasAncestor(Node node, string ancestorType)
    {
        var current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current.Type == ancestorType)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsDirectMemberOfOwner(Node memberNode, Node ownerNode)
    {
        var current = memberNode.Parent;
        while (!IsNullNode(current) && current != ownerNode)
        {
            if (current.Type is "function_item" or "function_signature_item" or "closure_expression"
                or "macro_invocation" or "token_tree")
            {
                return false;
            }

            current = current.Parent;
        }

        return current == ownerNode;
    }

    private static string DetermineSelfKind(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return "none";
        }

        if (parameters.Contains("&mut self", StringComparison.Ordinal))
        {
            return "&mut self";
        }

        if (parameters.Contains("&self", StringComparison.Ordinal))
        {
            return "&self";
        }

        if (Regex.IsMatch(parameters, @"\bself\b", RegexOptions.CultureInvariant))
        {
            return "self";
        }

        return "none";
    }

    private static string? ExtractUseAlias(string useText)
    {
        var match = Regex.Match(useText, @"\bas\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["alias"].Value : null;
    }

    private static string? ExtractDocComment(Node node, SourceContext source)
    {
        var lineIndex = source.GetLineIndex(node.StartIndex);
        if (lineIndex <= 0)
        {
            return null;
        }

        var comments = new List<string>();
        for (var i = lineIndex - 1; i >= 0; i--)
        {
            var trimmed = source.Lines[i].Trim();
            if (trimmed.Length == 0)
            {
                if (comments.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (trimmed.StartsWith("#[", StringComparison.Ordinal) || trimmed.StartsWith("#![", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                comments.Add(trimmed.Length > 3 ? trimmed[3..].TrimStart() : string.Empty);
                continue;
            }

            if (trimmed.StartsWith("//!", StringComparison.Ordinal))
            {
                comments.Add(trimmed.Length > 3 ? trimmed[3..].TrimStart() : string.Empty);
                continue;
            }

            if (trimmed.StartsWith("/**", StringComparison.Ordinal) || trimmed.StartsWith("/*!", StringComparison.Ordinal))
            {
                comments.Add(CleanBlockCommentLine(trimmed));
                continue;
            }

            break;
        }

        if (comments.Count == 0)
        {
            return null;
        }

        comments.Reverse();
        return string.Join("\n", comments.Where(c => !string.IsNullOrWhiteSpace(c)));
    }

    private static string CleanBlockCommentLine(string line)
    {
        var cleaned = line;
        cleaned = cleaned.Replace("/**", string.Empty, StringComparison.Ordinal)
            .Replace("/*!", string.Empty, StringComparison.Ordinal)
            .Replace("*/", string.Empty, StringComparison.Ordinal)
            .Trim();
        return cleaned;
    }

    private static Node TryGetField(Node node, string fieldName)
    {
        try
        {
            return node[fieldName];
        }
        catch (KeyNotFoundException)
        {
            return default;
        }
    }

    private static string NormalizeName(string text)
        => NormalizeWhitespace(text).Trim('"', '\'', ':');

    [GeneratedRegex(@"^r#[a-zA-Z_]\w*$|^[a-zA-Z_]\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex RustIdentifierPattern();

    /// <summary>
    /// Returns true if the name looks like a valid Rust identifier.
    /// Filters out phantom names produced by tree-sitter error recovery inside macro bodies.
    /// </summary>
    private static bool IsValidRustIdentifier(string name)
        => !string.IsNullOrEmpty(name) && RustIdentifierPattern().IsMatch(name);

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Extracts the bare type name from an impl target type node.
    /// For reference types like <c>&amp;'a mut Struct</c>, walks to the inner type_identifier or generic_type.
    /// For generic types like <c>Struct&lt;'a&gt;</c>, extracts just the type name.
    /// </summary>
    private static string NormalizeImplTargetType(Node targetTypeNode, SourceContext source)
    {
        var node = targetTypeNode;

        // Unwrap reference_type: &'a mut T -> T
        if (node.Type == "reference_type")
        {
            var inner = node.NamedChildren
                .FirstOrDefault(c => c.Type is "type_identifier" or "generic_type" or "scoped_type_identifier");
            if (!IsNullNode(inner))
            {
                node = inner;
            }
        }

        // For generic_type, extract just the type name (first type_identifier child)
        if (node.Type == "generic_type")
        {
            var typeId = node.NamedChildren
                .FirstOrDefault(c => c.Type is "type_identifier" or "scoped_type_identifier");
            if (!IsNullNode(typeId))
            {
                return NormalizeWhitespace(source.GetText(typeId));
            }
        }

        return NormalizeWhitespace(source.GetText(node));
    }

    /// <summary>
    /// Extracts the trait name from an impl trait node.
    /// For generic traits like <c>Trait&lt;'a, T&gt;</c>, extracts just the trait name.
    /// </summary>
    private static string? NormalizeImplTraitName(Node traitNameNode, SourceContext source)
    {
        if (IsNullNode(traitNameNode))
        {
            return null;
        }

        // For generic_type (e.g., Trait<'a>), extract just the type name
        if (traitNameNode.Type == "generic_type")
        {
            var typeId = traitNameNode.NamedChildren
                .FirstOrDefault(c => c.Type is "type_identifier" or "scoped_type_identifier");
            if (!IsNullNode(typeId))
            {
                return NormalizeWhitespace(source.GetText(typeId));
            }
        }

        // For scoped_type_identifier (e.g., std::fmt::Display), use full text
        return NormalizeWhitespace(source.GetText(traitNameNode));
    }

    private static string? NormalizeReturnType(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(returnType!);
        return normalized.StartsWith("->", StringComparison.Ordinal)
            ? normalized
            : $"-> {normalized}";
    }

    private static string? GetOptionalText(Node node, SourceContext source)
    {
        if (IsNullNode(node))
        {
            return null;
        }

        var text = NormalizeWhitespace(source.GetText(node));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? GetOptionalFieldText(Node node, string fieldName, SourceContext source)
    {
        var fieldNode = TryGetField(node, fieldName);
        return GetOptionalText(fieldNode, source);
    }

    private static Node GetCapture(IReadOnlyList<CaptureWithNode> captures, string name)
        => captures.FirstOrDefault(c => c.Name == name).Node;

    private static List<CaptureWithNode> ExecuteCapturesSafe(string query, Node rootNode)
    {
        try
        {
            return ExecuteCaptures(query, rootNode);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<CaptureWithNode> ExecuteCaptures(string query, Node rootNode)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        return cursor.Captures
            .Where(c => !c.Node.IsError)
            .Select(c => new CaptureWithNode(c.Name, c.Node))
            .ToList();
    }

    private static List<List<CaptureWithNode>> ExecuteMatchesSafe(string query, Node rootNode)
    {
        try
        {
            return ExecuteMatches(query, rootNode);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<List<CaptureWithNode>> ExecuteMatches(string query, Node rootNode)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        return cursor.Matches
            .Select(m => m.Captures
                .Where(c => !c.Node.IsError)
                .Select(c => new CaptureWithNode(c.Name, c.Node))
                .ToList())
            .Where(m => m.Count > 0)
            .ToList();
    }

    private static int CountErrorNodes(Node root)
    {
        var count = root.IsError ? 1 : 0;
        foreach (var child in root.NamedChildren)
        {
            count += CountErrorNodes(child);
        }

        return count;
    }

    private static int CountLines(string sourceCode)
    {
        if (sourceCode.Length == 0)
        {
            return 0;
        }

        var lineCount = 1;
        for (var i = 0; i < sourceCode.Length; i++)
        {
            if (sourceCode[i] == '\n')
            {
                lineCount++;
            }
        }

        return lineCount;
    }

    private static List<string> SplitTopLevelCommaSeparated(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
            }

            if (ch == ',' && angleDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                var part = current.ToString().Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }

                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        return parts;
    }

    private static bool IsNullNode(Node? node)
        => node is null || node.Id == IntPtr.Zero;

    private static string GetNodeKey(Node node)
        => $"{node.StartIndex}:{node.EndIndex}:{node.Type}";

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-rust", "tree_sitter_rust");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter Rust grammar from TreeSitter.DotNet. Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RustTreeSitterClient));
        }
    }

    private readonly record struct CaptureWithNode(string Name, Node Node);

    private sealed class SourceContext
    {
        public SourceContext(string sourceCode)
        {
            SourceCode = sourceCode;
            Utf8Bytes = Encoding.UTF8.GetBytes(sourceCode);

            var starts = new List<int> { 0 };
            for (var i = 0; i < Utf8Bytes.Length; i++)
            {
                if (Utf8Bytes[i] == (byte)'\n' && i + 1 <= Utf8Bytes.Length)
                {
                    starts.Add(i + 1);
                }
            }

            LineStarts = starts.ToArray();
            Lines = new string[LineStarts.Length];

            for (var i = 0; i < LineStarts.Length; i++)
            {
                var start = LineStarts[i];
                var endExclusive = GetLineEndExclusive(i);
                var length = Math.Max(0, endExclusive - start);
                var text = length == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(Utf8Bytes, start, length);
                Lines[i] = text.TrimEnd('\r');
            }
        }

        public string SourceCode { get; }

        public byte[] Utf8Bytes { get; }

        public int[] LineStarts { get; }

        public string[] Lines { get; }

        public string GetText(RustByteRange byteRange)
            => GetText(byteRange.StartByte, byteRange.EndByte);

        public string GetText(Node node)
            => GetText(node.StartIndex, node.EndIndex);

        public string GetText(int startByte, int endByte)
        {
            if (Utf8Bytes.Length == 0)
            {
                return string.Empty;
            }

            var safeStart = Math.Clamp(startByte, 0, Utf8Bytes.Length);
            var safeEnd = Math.Clamp(endByte, safeStart, Utf8Bytes.Length);
            if (safeEnd <= safeStart)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(Utf8Bytes, safeStart, safeEnd - safeStart);
        }

        public int GetLineIndex(int byteOffset)
        {
            if (LineStarts.Length == 0)
            {
                return 0;
            }

            var index = Array.BinarySearch(LineStarts, byteOffset);
            if (index >= 0)
            {
                return index;
            }

            index = ~index - 1;
            return Math.Clamp(index, 0, LineStarts.Length - 1);
        }

        public int GetLineStart(int lineIndex)
            => LineStarts[Math.Clamp(lineIndex, 0, LineStarts.Length - 1)];

        public int GetLineEndExclusive(int lineIndex)
        {
            var safeLine = Math.Clamp(lineIndex, 0, LineStarts.Length - 1);
            if (safeLine + 1 < LineStarts.Length)
            {
                return Math.Max(LineStarts[safeLine], LineStarts[safeLine + 1] - 1);
            }

            return Utf8Bytes.Length;
        }
    }
}
