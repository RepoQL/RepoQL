using RepoQL.Formats.Go.Surface;
using System.Text.RegularExpressions;
using TreeSitter;

namespace RepoQL.Formats.Go.TreeSitter;

public sealed class GoTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private static readonly Query SharedCombinedQuery = SharedLanguage.CreateQuery(GoQueries.CombinedQuery);
    private static readonly Regex SentinelFactoryRegex = new(
        @"^(?:errors\.New|fmt\.Errorf)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SentinelAddressRegex = new(
        @"^&[A-Za-z_][A-Za-z0-9_\.]*Error\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InterfaceNilAssertionRegex = new(
        @"^\(\s*\*?(?<type>[A-Za-z_][A-Za-z0-9_\.]*)\s*\)\s*\(\s*nil\s*\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InterfaceLiteralAssertionRegex = new(
        @"^&?(?<type>[A-Za-z_][A-Za-z0-9_\.]*)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public GoDocumentSurface Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new GoDocumentSurface(
                PackageName: null,
                Imports: [],
                Structs: [],
                Interfaces: [],
                TypeDefinitions: [],
                Constants: [],
                ConstantBlocks: [],
                Variables: [],
                Directives: [],
                Functions: [],
                InitFunctions: [],
                Methods: [],
                Stats: new GoParseStats(0, 0, 0, 0, 0, 0),
                ErrorNodeCount: 0);
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;

            var errorNodeCount = CountErrorNodes(root);
            var packageName = ExtractPackageName(root);
            var imports = ExtractImports(root);
            var structs = ExtractStructs(root);
            var interfaces = ExtractInterfaces(root);
            var typeDefinitions = ExtractTypeDefinitions(root);
            var constants = ExtractConstants(root, out var constantBlocks);
            var variables = ExtractVariables(root);
            var functions = ExtractFunctions(root);
            var initFunctions = functions
                .Where(f => string.Equals(f.Name, "init", StringComparison.Ordinal))
                .ToList();
            var methods = ExtractMethods(root);
            var directives = ExtractDirectives(root);
            directives.AddRange(ExtractConcurrencyDirectives(root));
            directives = directives
                .OrderBy(d => d.ByteRange.StartByte)
                .ToList();

            return new GoDocumentSurface(
                PackageName: packageName,
                Imports: imports,
                Structs: structs,
                Interfaces: interfaces,
                TypeDefinitions: typeDefinitions,
                Constants: constants,
                ConstantBlocks: constantBlocks,
                Variables: variables,
                Directives: directives,
                Functions: functions,
                InitFunctions: initFunctions,
                Methods: methods,
                Stats: new GoParseStats(
                    StructCount: structs.Count,
                    InterfaceCount: interfaces.Count,
                    FunctionCount: functions.Count,
                    MethodCount: methods.Count,
                    ImportCount: imports.Count,
                    LineCount: CountLines(sourceCode)),
                ErrorNodeCount: errorNodeCount);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native Go parser (tree-sitter-go). Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    public IReadOnlyList<GoQueryCaptureGroup> ExecuteQuery(string query, string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(sourceCode))
        {
            return [];
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            return ExecuteQuery(query, tree.RootNode);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native Go parser (tree-sitter-go). Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
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

    private static IReadOnlyList<GoQueryCaptureGroup> ExecuteQuery(string query, Node rootNode)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        var groups = new List<GoQueryCaptureGroup>();

        foreach (var match in cursor.Matches)
        {
            var captures = match.Captures
                .Where(c => !c.Node.IsError)
                .Select(c => new GoQueryCapture(
                    c.Name,
                    c.Node.Text,
                    new GoByteRange(c.Node.StartIndex, c.Node.EndIndex)))
                .ToList();

            if (captures.Count == 0)
            {
                continue;
            }

            groups.Add(new GoQueryCaptureGroup(match.PatternIndex, captures));
        }

        return groups;
    }

    private static string? ExtractPackageName(Node root)
    {
        var captures = ExecuteCaptures(GoQueries.PackageClause, root);
        var packageNode = GetCaptureNode(captures, "package_name");
        return IsNullNode(packageNode) ? null : NormalizeName(packageNode!.Text);
    }

    private static List<GoImportInfo> ExtractImports(Node root)
    {
        var imports = new List<GoImportInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.ImportSpecs, root))
        {
            var specNode = GetCaptureNode(match, "import_spec");
            var pathNode = GetCaptureNode(match, "import_path");
            if (IsNullNode(specNode) || IsNullNode(pathNode))
            {
                continue;
            }

            var specKey = GetNodeKey(specNode!);
            if (!seen.Add(specKey))
            {
                continue;
            }

            var path = NormalizeStringLiteral(pathNode!.Text);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var aliasNode = GetCaptureNode(match, "import_alias");
            var alias = IsNullNode(aliasNode) ? null : NormalizeName(aliasNode!.Text);

            imports.Add(new GoImportInfo(
                Path: path,
                Alias: alias,
                Category: ClassifyImport(path),
                ByteRange: new GoByteRange(specNode.StartIndex, specNode.EndIndex)));
        }

        return imports
            .OrderBy(i => i.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoStructInfo> ExtractStructs(Node root)
    {
        var builders = new Dictionary<string, StructBuilder>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.StructDeclarations, root))
        {
            var structDeclNode = GetCaptureNode(match, "struct_decl");
            var structTypeNode = GetCaptureNode(match, "struct_type");
            var structNameNode = GetCaptureNode(match, "struct_name");
            if (IsNullNode(structDeclNode) || IsNullNode(structTypeNode) || IsNullNode(structNameNode))
            {
                continue;
            }

            var name = NormalizeName(structNameNode!.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = GetNodeKey(structTypeNode!);
            builders[key] = new StructBuilder(
                name,
                IsExportedName(name),
                new GoByteRange(structDeclNode!.StartIndex, structDeclNode.EndIndex));
        }

        foreach (var match in ExecuteMatches(GoQueries.StructFields, root))
        {
            var structTypeNode = GetCaptureNode(match, "struct_type");
            var fieldNode = GetCaptureNode(match, "struct_field");
            if (IsNullNode(structTypeNode) || IsNullNode(fieldNode))
            {
                continue;
            }

            var key = GetNodeKey(structTypeNode!);
            if (!builders.TryGetValue(key, out var builder))
            {
                continue;
            }

            foreach (var field in CreateFields(fieldNode!))
            {
                builder.Fields.Add(field);
            }
        }

        return builders.Values
            .Select(b => b.ToSurface())
            .OrderBy(s => s.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoFieldInfo> CreateFields(Node fieldNode)
    {
        var typeNode = TryGetField(fieldNode, "type");
        if (IsNullNode(typeNode))
        {
            return [];
        }

        var typeName = NormalizeWhitespace(typeNode!.Text);
        var tagNode = TryGetField(fieldNode, "tag");
        var tag = IsNullNode(tagNode) ? null : NormalizeStringLiteral(tagNode!.Text);
        var byteRange = new GoByteRange(fieldNode.StartIndex, fieldNode.EndIndex);

        var names = fieldNode.NamedChildren
            .Where(n => n.Type == "field_identifier")
            .Select(n => NormalizeName(n.Text))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            var embeddedName = DeriveEmbeddedFieldName(typeName);
            return
            [
                new GoFieldInfo(
                    Name: embeddedName,
                    TypeName: typeName,
                    Tag: tag,
                    IsEmbedded: true,
                    IsExported: IsExportedName(embeddedName),
                    ByteRange: byteRange)
            ];
        }

        return names
            .Select(name => new GoFieldInfo(
                Name: name,
                TypeName: typeName,
                Tag: tag,
                IsEmbedded: false,
                IsExported: IsExportedName(name),
                ByteRange: byteRange))
            .ToList();
    }

    private static List<GoInterfaceInfo> ExtractInterfaces(Node root)
    {
        var builders = new Dictionary<string, InterfaceBuilder>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.InterfaceDeclarations, root))
        {
            var interfaceDeclNode = GetCaptureNode(match, "interface_decl");
            var interfaceTypeNode = GetCaptureNode(match, "interface_type");
            var interfaceNameNode = GetCaptureNode(match, "interface_name");
            if (IsNullNode(interfaceDeclNode) || IsNullNode(interfaceTypeNode) || IsNullNode(interfaceNameNode))
            {
                continue;
            }

            var name = NormalizeName(interfaceNameNode!.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = GetNodeKey(interfaceTypeNode!);
            builders[key] = new InterfaceBuilder(
                name,
                IsExportedName(name),
                new GoByteRange(interfaceDeclNode!.StartIndex, interfaceDeclNode.EndIndex));
        }

        foreach (var match in ExecuteMatches(GoQueries.InterfaceMethods, root))
        {
            var methodNode = GetCaptureNode(match, "interface_method");
            var nameNode = GetCaptureNode(match, "interface_method_name");
            var parametersNode = GetCaptureNode(match, "interface_method_parameters");
            if (IsNullNode(methodNode) || IsNullNode(nameNode) || IsNullNode(parametersNode))
            {
                continue;
            }

            var interfaceTypeNode = FindAncestor(methodNode!, "interface_type");
            if (IsNullNode(interfaceTypeNode))
            {
                continue;
            }

            var interfaceKey = GetNodeKey(interfaceTypeNode!);
            if (!builders.TryGetValue(interfaceKey, out var builder))
            {
                continue;
            }

            var name = NormalizeName(nameNode!.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var resultNode = GetCaptureNode(match, "interface_method_result");
            var returnType = IsNullNode(resultNode) ? null : NormalizeWhitespace(resultNode!.Text);

            builder.Methods.Add(new GoInterfaceMethodInfo(
                Name: name,
                Parameters: NormalizeWhitespace(parametersNode!.Text),
                ReturnType: returnType,
                ByteRange: new GoByteRange(methodNode.StartIndex, methodNode.EndIndex)));
        }

        foreach (var match in ExecuteMatches(GoQueries.EmbeddedInterfaces, root))
        {
            var embeddedTypeNode = GetCaptureNode(match, "embedded_interface_type");
            var embeddedContainerNode = GetCaptureNode(match, "embedded_interface");
            if (IsNullNode(embeddedTypeNode) || IsNullNode(embeddedContainerNode))
            {
                continue;
            }

            var interfaceTypeNode = FindAncestor(embeddedContainerNode!, "interface_type");
            if (IsNullNode(interfaceTypeNode))
            {
                continue;
            }

            var interfaceKey = GetNodeKey(interfaceTypeNode!);
            if (!builders.TryGetValue(interfaceKey, out var builder))
            {
                continue;
            }

            var text = NormalizeWhitespace(embeddedTypeNode!.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var candidate in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!builder.EmbeddedInterfaces.Contains(candidate, StringComparer.Ordinal))
                {
                    builder.EmbeddedInterfaces.Add(candidate);
                }
            }
        }

        return builders.Values
            .Select(b => b.ToSurface())
            .OrderBy(i => i.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoTypeDefinitionInfo> ExtractTypeDefinitions(Node root)
    {
        var types = new List<GoTypeDefinitionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.TypeDefinitions, root))
        {
            var nameNode = GetCaptureNode(match, "type_name");
            var underlyingNode = GetCaptureNode(match, "type_underlying");
            if (IsNullNode(nameNode) || IsNullNode(underlyingNode))
            {
                continue;
            }

            var typeSpecNode = GetCaptureNode(match, "type_spec");
            var typeAliasNode = GetCaptureNode(match, "type_alias");
            var declarationNode = !IsNullNode(typeAliasNode) ? typeAliasNode! : typeSpecNode;
            if (IsNullNode(declarationNode))
            {
                continue;
            }

            var isAlias = !IsNullNode(typeAliasNode) || string.Equals(declarationNode!.Type, "type_alias", StringComparison.Ordinal);
            if (!isAlias
                && (string.Equals(underlyingNode!.Type, "struct_type", StringComparison.Ordinal)
                    || string.Equals(underlyingNode.Type, "interface_type", StringComparison.Ordinal)))
            {
                continue;
            }

            var key = GetNodeKey(declarationNode!);
            if (!seen.Add(key))
            {
                continue;
            }

            var name = NormalizeName(nameNode!.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            types.Add(new GoTypeDefinitionInfo(
                Name: name,
                UnderlyingType: NormalizeWhitespace(underlyingNode.Text),
                IsAlias: isAlias,
                IsExported: IsExportedName(name),
                ByteRange: new GoByteRange(declarationNode.StartIndex, declarationNode.EndIndex)));
        }

        return types
            .OrderBy(t => t.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoConstantInfo> ExtractConstants(Node root, out List<GoConstantBlockInfo> constantBlocks)
    {
        var specBuilders = new Dictionary<string, ConstantSpecBuilder>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.ConstantSpecs, root))
        {
            var specNode = GetCaptureNode(match, "const_spec");
            if (IsNullNode(specNode))
            {
                continue;
            }

            var specKey = GetNodeKey(specNode!);
            if (!specBuilders.TryGetValue(specKey, out var builder))
            {
                builder = new ConstantSpecBuilder(
                    specNode!,
                    FindAncestor(specNode!, "const_declaration"));
                specBuilders[specKey] = builder;
            }

            var typeNode = GetCaptureNode(match, "const_type");
            if (!IsNullNode(typeNode))
            {
                builder.TypeName = NormalizeWhitespace(typeNode!.Text);
            }

            var valueNode = GetCaptureNode(match, "const_value");
            if (!IsNullNode(valueNode))
            {
                builder.Value = NormalizeWhitespace(valueNode!.Text);
            }

            foreach (var nameNode in GetCaptureNodes(match, "const_name"))
            {
                var name = NormalizeName(nameNode.Text);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                builder.AddName(name, new GoByteRange(nameNode.StartIndex, nameNode.EndIndex));
            }
        }

        var constants = new List<GoConstantInfo>();
        constantBlocks = new List<GoConstantBlockInfo>();

        foreach (var group in specBuilders.Values
                     .GroupBy(s => s.DeclarationNode is null ? GetNodeKey(s.SpecNode) : GetNodeKey(s.DeclarationNode!))
                     .OrderBy(g => g.Min(s => s.SpecNode.StartIndex)))
        {
            var specs = group
                .OrderBy(s => s.SpecNode.StartIndex)
                .ToList();

            if (specs.Count == 0)
            {
                continue;
            }

            var first = specs[0];
            var enumType = string.IsNullOrWhiteSpace(first.TypeName) ? null : first.TypeName;
            var hasIota = !string.IsNullOrWhiteSpace(enumType) && ContainsIota(first.Value);

            var blockTypeName = hasIota ? enumType : null;
            var blockConstants = new List<GoConstantInfo>();
            foreach (var spec in specs)
            {
                if (spec.Names.Count == 0)
                {
                    continue;
                }

                var effectiveType = hasIota && string.IsNullOrWhiteSpace(spec.TypeName)
                    ? enumType
                    : spec.TypeName;

                var values = SplitExpressionList(spec.Value);
                for (var i = 0; i < spec.Names.Count; i++)
                {
                    var nameCapture = spec.Names[i];
                    var value = ResolveExpressionValue(values, spec.Value, i, spec.Names.Count);

                    var constant = new GoConstantInfo(
                        Name: nameCapture.Name,
                        TypeName: effectiveType,
                        Value: value,
                        IsExported: IsExportedName(nameCapture.Name),
                        ByteRange: nameCapture.ByteRange);

                    constants.Add(constant);
                    blockConstants.Add(constant);
                }
            }

            if (blockConstants.Count == 0)
            {
                continue;
            }

            if (specs.Count > 1 || hasIota)
            {
                var rangeSource = first.DeclarationNode ?? first.SpecNode;
                constantBlocks.Add(new GoConstantBlockInfo(
                    Constants: blockConstants.OrderBy(c => c.ByteRange.StartByte).ToList(),
                    TypeName: blockTypeName,
                    HasIota: hasIota,
                    ByteRange: new GoByteRange(rangeSource.StartIndex, rangeSource.EndIndex)));
            }
        }

        constants = constants
            .OrderBy(c => c.ByteRange.StartByte)
            .ToList();

        constantBlocks = constantBlocks
            .OrderBy(c => c.ByteRange.StartByte)
            .ToList();

        return constants;
    }

    private static List<GoVariableInfo> ExtractVariables(Node root)
    {
        var specBuilders = new Dictionary<string, VariableSpecBuilder>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.VariableSpecs, root))
        {
            var specNode = GetCaptureNode(match, "var_spec");
            if (IsNullNode(specNode))
            {
                continue;
            }

            var specKey = GetNodeKey(specNode!);
            if (!specBuilders.TryGetValue(specKey, out var builder))
            {
                builder = new VariableSpecBuilder(specNode!);
                specBuilders[specKey] = builder;
            }

            var typeNode = GetCaptureNode(match, "var_type");
            if (!IsNullNode(typeNode))
            {
                builder.TypeName = NormalizeWhitespace(typeNode!.Text);
            }

            var valueNode = GetCaptureNode(match, "var_value");
            if (!IsNullNode(valueNode))
            {
                builder.Value = NormalizeWhitespace(valueNode!.Text);
            }

            foreach (var nameNode in GetCaptureNodes(match, "var_name"))
            {
                var name = NormalizeName(nameNode.Text);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                builder.AddName(name, new GoByteRange(nameNode.StartIndex, nameNode.EndIndex));
            }
        }

        var variables = new List<GoVariableInfo>();
        foreach (var spec in specBuilders.Values.OrderBy(s => s.SpecNode.StartIndex))
        {
            if (spec.Names.Count == 0)
            {
                continue;
            }

            var values = SplitExpressionList(spec.Value);
            for (var i = 0; i < spec.Names.Count; i++)
            {
                var nameCapture = spec.Names[i];
                var value = ResolveExpressionValue(values, spec.Value, i, spec.Names.Count);
                var isSentinel = IsSentinelErrorValue(nameCapture.Name, value);
                var (isAssertion, assertedInterface, assertedType) = DetectInterfaceAssertion(
                    nameCapture.Name,
                    spec.TypeName,
                    value);

                variables.Add(new GoVariableInfo(
                    Name: nameCapture.Name,
                    TypeName: spec.TypeName,
                    Value: value,
                    IsExported: IsExportedName(nameCapture.Name),
                    IsSentinelError: isSentinel,
                    IsInterfaceAssertion: isAssertion,
                    AssertedInterface: assertedInterface,
                    AssertedType: assertedType,
                    ByteRange: nameCapture.ByteRange));
            }
        }

        return variables
            .OrderBy(v => v.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoDirectiveInfo> ExtractDirectives(Node root)
    {
        var directives = new List<GoDirectiveInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.Comments, root))
        {
            var commentNode = GetCaptureNode(match, "comment");
            if (IsNullNode(commentNode))
            {
                continue;
            }

            if (!TryParseDirective(commentNode!.Text, out var kind, out var text))
            {
                continue;
            }

            var key = $"{kind}:{commentNode.StartIndex}:{commentNode.EndIndex}";
            if (!seen.Add(key))
            {
                continue;
            }

            directives.Add(new GoDirectiveInfo(
                Kind: kind,
                Text: text,
                ByteRange: new GoByteRange(commentNode.StartIndex, commentNode.EndIndex)));
        }

        return directives
            .OrderBy(d => d.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoDirectiveInfo> ExtractConcurrencyDirectives(Node root)
    {
        var directives = new List<GoDirectiveInfo>();

        foreach (var match in ExecuteMatches(GoQueries.GoStatements, root))
        {
            var statementNode = GetCaptureNode(match, "goroutine_stmt");
            if (IsNullNode(statementNode))
            {
                continue;
            }

            var expressionNode = GetCaptureNode(match, "goroutine_expr");
            var text = IsNullNode(expressionNode)
                ? NormalizeWhitespace(statementNode!.Text)
                : NormalizeWhitespace(expressionNode!.Text);

            directives.Add(new GoDirectiveInfo(
                Kind: "goroutine",
                Text: text,
                ByteRange: new GoByteRange(statementNode!.StartIndex, statementNode.EndIndex)));
        }

        foreach (var match in ExecuteMatches(GoQueries.ChannelTypes, root))
        {
            var channelNode = GetCaptureNode(match, "channel_type");
            if (IsNullNode(channelNode))
            {
                continue;
            }

            directives.Add(new GoDirectiveInfo(
                Kind: "channel",
                Text: NormalizeWhitespace(channelNode!.Text),
                ByteRange: new GoByteRange(channelNode.StartIndex, channelNode.EndIndex)));
        }

        foreach (var match in ExecuteMatches(GoQueries.SelectStatements, root))
        {
            var selectNode = GetCaptureNode(match, "select_stmt");
            if (IsNullNode(selectNode))
            {
                continue;
            }

            directives.Add(new GoDirectiveInfo(
                Kind: "select",
                Text: "select",
                ByteRange: new GoByteRange(selectNode!.StartIndex, selectNode.EndIndex)));
        }

        return directives
            .OrderBy(d => d.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoFunctionInfo> ExtractFunctions(Node root)
    {
        var functions = new List<GoFunctionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.FunctionDeclarations, root))
        {
            var functionNode = GetCaptureNode(match, "function_decl");
            var nameNode = GetCaptureNode(match, "function_name");
            var parametersNode = GetCaptureNode(match, "function_parameters");
            if (IsNullNode(functionNode) || IsNullNode(nameNode) || IsNullNode(parametersNode))
            {
                continue;
            }

            var key = GetNodeKey(functionNode!);
            if (!seen.Add(key))
            {
                continue;
            }

            var name = NormalizeName(nameNode!.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var resultNode = GetCaptureNode(match, "function_result");
            var returnType = IsNullNode(resultNode) ? null : NormalizeWhitespace(resultNode!.Text);

            functions.Add(new GoFunctionInfo(
                Name: name,
                IsExported: IsExportedName(name),
                Parameters: NormalizeWhitespace(parametersNode!.Text),
                ReturnType: returnType,
                ByteRange: new GoByteRange(functionNode.StartIndex, functionNode.EndIndex)));
        }

        return functions
            .OrderBy(f => f.ByteRange.StartByte)
            .ToList();
    }

    private static List<GoMethodInfo> ExtractMethods(Node root)
    {
        var methods = new List<GoMethodInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(GoQueries.MethodDeclarations, root))
        {
            var methodNode = GetCaptureNode(match, "method_decl");
            var nameNode = GetCaptureNode(match, "method_name");
            var receiverNode = GetCaptureNode(match, "method_receiver");
            var parametersNode = GetCaptureNode(match, "method_parameters");
            if (IsNullNode(methodNode) || IsNullNode(nameNode) || IsNullNode(receiverNode) || IsNullNode(parametersNode))
            {
                continue;
            }

            var key = GetNodeKey(methodNode!);
            if (!seen.Add(key))
            {
                continue;
            }

            var methodName = NormalizeName(nameNode!.Text);
            if (string.IsNullOrWhiteSpace(methodName))
            {
                continue;
            }

            var receiver = ParseReceiver(receiverNode!);
            var resultNode = GetCaptureNode(match, "method_result");
            var returnType = IsNullNode(resultNode) ? null : NormalizeWhitespace(resultNode!.Text);

            methods.Add(new GoMethodInfo(
                Name: methodName,
                IsExported: IsExportedName(methodName),
                ReceiverName: receiver.Name,
                ReceiverType: receiver.Type,
                IsPointerReceiver: receiver.IsPointer,
                Parameters: NormalizeWhitespace(parametersNode!.Text),
                ReturnType: returnType,
                ByteRange: new GoByteRange(methodNode.StartIndex, methodNode.EndIndex)));
        }

        return methods
            .OrderBy(m => m.ByteRange.StartByte)
            .ToList();
    }

    private static ReceiverInfo ParseReceiver(Node receiverNode)
    {
        var receiverDecl = receiverNode.NamedChildren
            .FirstOrDefault(n => n.Type is "parameter_declaration" or "variadic_parameter_declaration");

        if (IsNullNode(receiverDecl))
        {
            return new ReceiverInfo(string.Empty, string.Empty, false);
        }

        var nameNode = TryGetField(receiverDecl, "name");
        var typeNode = TryGetField(receiverDecl, "type");

        var receiverName = IsNullNode(nameNode) ? string.Empty : NormalizeName(nameNode!.Text);
        var rawType = IsNullNode(typeNode) ? string.Empty : NormalizeWhitespace(typeNode!.Text);
        var isPointer = rawType.StartsWith('*');
        var receiverType = isPointer ? NormalizeWhitespace(rawType[1..]) : rawType;

        if (string.IsNullOrWhiteSpace(receiverType))
        {
            receiverType = DeriveEmbeddedFieldName(rawType);
        }

        if (string.IsNullOrWhiteSpace(receiverName))
        {
            receiverName = DeriveEmbeddedFieldName(receiverType);
        }

        return new ReceiverInfo(receiverName, receiverType, isPointer);
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

    private static bool ContainsIota(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("iota", StringComparison.Ordinal);

    private static string? ResolveExpressionValue(
        IReadOnlyList<string> splitValues,
        string? rawValue,
        int index,
        int nameCount)
    {
        if (splitValues.Count == 0)
        {
            return null;
        }

        if (splitValues.Count == nameCount && index < splitValues.Count)
        {
            return splitValues[index];
        }

        if (splitValues.Count == 1)
        {
            return splitValues[0];
        }

        if (index < splitValues.Count)
        {
            return splitValues[index];
        }

        return string.IsNullOrWhiteSpace(rawValue) ? null : NormalizeWhitespace(rawValue);
    }

    private static List<string> SplitExpressionList(string? expressionList)
    {
        if (string.IsNullOrWhiteSpace(expressionList))
        {
            return [];
        }

        var values = new List<string>();
        var buffer = new System.Text.StringBuilder();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        var escaped = false;

        foreach (var ch in expressionList)
        {
            buffer.Append(ch);

            if (quote != '\0')
            {
                if (quote != '`')
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                }

                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'' or '`')
            {
                quote = ch;
                continue;
            }

            switch (ch)
            {
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    continue;
                case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    var segment = buffer.ToString();
                    if (segment.EndsWith(','))
                    {
                        segment = segment[..^1];
                    }

                    var normalized = NormalizeWhitespace(segment);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        values.Add(normalized);
                    }

                    buffer.Clear();
                    continue;
            }
        }

        var trailing = NormalizeWhitespace(buffer.ToString());
        if (!string.IsNullOrWhiteSpace(trailing))
        {
            values.Add(trailing);
        }

        return values;
    }

    private static bool IsSentinelErrorValue(string name, string? value)
    {
        if (!name.StartsWith("Err", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeWhitespace(value);
        return SentinelFactoryRegex.IsMatch(normalized)
               || SentinelAddressRegex.IsMatch(normalized);
    }

    private static (bool IsAssertion, string? AssertedInterface, string? AssertedType) DetectInterfaceAssertion(
        string name,
        string? typeName,
        string? value)
    {
        if (!string.Equals(name, "_", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(typeName)
            || string.IsNullOrWhiteSpace(value))
        {
            return (false, null, null);
        }

        var assertedInterface = NormalizeWhitespace(typeName);
        var normalized = NormalizeWhitespace(value);

        var nilMatch = InterfaceNilAssertionRegex.Match(normalized);
        if (nilMatch.Success)
        {
            return (true, assertedInterface, nilMatch.Groups["type"].Value);
        }

        var literalMatch = InterfaceLiteralAssertionRegex.Match(normalized);
        if (literalMatch.Success)
        {
            return (true, assertedInterface, literalMatch.Groups["type"].Value);
        }

        return (false, null, null);
    }

    private static bool TryParseDirective(string commentText, out string kind, out string text)
    {
        kind = string.Empty;
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(commentText))
        {
            return false;
        }

        var lines = commentText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                line = line[2..].TrimStart();
            }
            else if (line.StartsWith("*", StringComparison.Ordinal))
            {
                line = line[1..].TrimStart();
            }
            else if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                line = line[2..].TrimStart();
            }

            line = line.TrimEnd('/').TrimEnd('*').Trim();
            if (!line.StartsWith("go:", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("go:build", StringComparison.Ordinal))
            {
                kind = "build";
            }
            else if (line.StartsWith("go:embed", StringComparison.Ordinal))
            {
                kind = "embed";
            }
            else if (line.StartsWith("go:generate", StringComparison.Ordinal))
            {
                kind = "generate";
            }
            else if (line.StartsWith("go:linkname", StringComparison.Ordinal))
            {
                kind = "linkname";
            }
            else
            {
                continue;
            }

            text = $"//{line}";
            return true;
        }

        return false;
    }

    private static int CountErrorNodes(Node root)
    {
        var count = (root.IsError || root.IsMissing) ? 1 : 0;
        foreach (var child in root.NamedChildren)
        {
            count += CountErrorNodes(child);
        }

        if (count == 0 && root.HasError)
        {
            return 1;
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

    private static Node? TryGetField(Node node, string fieldName)
    {
        try
        {
            return node[fieldName];
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static Node? GetCaptureNode(IEnumerable<CaptureWithNode> captures, string name)
        => captures.FirstOrDefault(c => c.Name == name).Node;

    private static IReadOnlyList<Node> GetCaptureNodes(IEnumerable<CaptureWithNode> captures, string name)
        => captures
            .Where(c => c.Name == name && !IsNullNode(c.Node))
            .Select(c => c.Node)
            .ToList();

    private static Node? FindAncestor(Node node, string type)
    {
        Node? current = node;
        while (!IsNullNode(current))
        {
            if (current!.Type == type)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsNullNode(Node? node)
        => node is null || node.Id == IntPtr.Zero;

    private static string NormalizeName(string text)
        => text.Trim().Trim('"', '\'', '`');

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeStringLiteral(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '`' && value[^1] == '`')))
        {
            value = value[1..^1];
        }

        return value;
    }

    private static bool IsExportedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        return trimmed.Length > 0 && char.IsUpper(trimmed[0]);
    }

    private static string ClassifyImport(string path)
        => path.Contains('.', StringComparison.Ordinal) ? "external" : "stdlib";

    private static string DeriveEmbeddedFieldName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        var value = typeName.Trim();
        while (value.StartsWith('*'))
        {
            value = value[1..].TrimStart();
        }

        var genericStart = value.IndexOf('[', StringComparison.Ordinal);
        if (genericStart >= 0)
        {
            value = value[..genericStart];
        }

        var dotIndex = value.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex < value.Length - 1)
        {
            value = value[(dotIndex + 1)..];
        }

        return value.Trim();
    }

    private static string GetNodeKey(Node node)
        => $"{node.StartIndex}:{node.EndIndex}:{node.Type}";

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-go", "tree_sitter_go");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter Go grammar from TreeSitter.DotNet (tree-sitter-go). Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GoTreeSitterClient));
        }
    }

    private readonly record struct CaptureWithNode(string Name, Node Node);

    private readonly record struct ReceiverInfo(string Name, string Type, bool IsPointer);

    private readonly record struct NameCapture(string Name, GoByteRange ByteRange);

    private sealed class ConstantSpecBuilder
    {
        private readonly HashSet<string> _seenNames = new(StringComparer.Ordinal);

        public ConstantSpecBuilder(Node specNode, Node? declarationNode)
        {
            SpecNode = specNode;
            DeclarationNode = declarationNode;
        }

        public Node SpecNode { get; }
        public Node? DeclarationNode { get; }
        public string? TypeName { get; set; }
        public string? Value { get; set; }
        public List<NameCapture> Names { get; } = [];

        public void AddName(string name, GoByteRange byteRange)
        {
            if (_seenNames.Add(name))
            {
                Names.Add(new NameCapture(name, byteRange));
            }
        }
    }

    private sealed class VariableSpecBuilder
    {
        private readonly HashSet<string> _seenNames = new(StringComparer.Ordinal);

        public VariableSpecBuilder(Node specNode)
        {
            SpecNode = specNode;
        }

        public Node SpecNode { get; }
        public string? TypeName { get; set; }
        public string? Value { get; set; }
        public List<NameCapture> Names { get; } = [];

        public void AddName(string name, GoByteRange byteRange)
        {
            if (_seenNames.Add(name))
            {
                Names.Add(new NameCapture(name, byteRange));
            }
        }
    }

    private sealed class StructBuilder
    {
        public StructBuilder(string name, bool isExported, GoByteRange byteRange)
        {
            Name = name;
            IsExported = isExported;
            ByteRange = byteRange;
        }

        public string Name { get; }
        public bool IsExported { get; }
        public GoByteRange ByteRange { get; }
        public List<GoFieldInfo> Fields { get; } = [];

        public GoStructInfo ToSurface()
            => new(
                Name: Name,
                IsExported: IsExported,
                Fields: Fields.OrderBy(f => f.ByteRange.StartByte).ToList(),
                ByteRange: ByteRange);
    }

    private sealed class InterfaceBuilder
    {
        public InterfaceBuilder(string name, bool isExported, GoByteRange byteRange)
        {
            Name = name;
            IsExported = isExported;
            ByteRange = byteRange;
        }

        public string Name { get; }
        public bool IsExported { get; }
        public GoByteRange ByteRange { get; }
        public List<GoInterfaceMethodInfo> Methods { get; } = [];
        public List<string> EmbeddedInterfaces { get; } = [];

        public GoInterfaceInfo ToSurface()
            => new(
                Name: Name,
                IsExported: IsExported,
                Methods: Methods.OrderBy(m => m.ByteRange.StartByte).ToList(),
                EmbeddedInterfaces: EmbeddedInterfaces,
                ByteRange: ByteRange);
    }

    /// <summary>
    /// Executes <see cref="SharedCombinedQuery"/> once against <paramref name="root"/> and dispatches
    /// each match into the appropriate group bucket based on <see cref="GoQueries.ClassifyPattern"/>.
    /// </summary>
    private static DispatchedMatches ExecuteCombinedQuery(Node root)
    {
        var result = new DispatchedMatches();
        using var cursor = SharedCombinedQuery.Execute(root);

        foreach (var match in cursor.Matches)
        {
            var captures = match.Captures
                .Where(c => !c.Node.IsError)
                .Select(c => new CaptureWithNode(c.Name, c.Node))
                .ToList();

            if (captures.Count == 0)
            {
                continue;
            }

            var group = GoQueries.ClassifyPattern(match.PatternIndex);
            var bucket = group switch
            {
                GoPatternGroup.PackageClause => result.PackageClause,
                GoPatternGroup.ImportSpecs => result.ImportSpecs,
                GoPatternGroup.StructDeclarations => result.StructDeclarations,
                GoPatternGroup.StructFields => result.StructFields,
                GoPatternGroup.InterfaceDeclarations => result.InterfaceDeclarations,
                GoPatternGroup.InterfaceMethods => result.InterfaceMethods,
                GoPatternGroup.EmbeddedInterfaces => result.EmbeddedInterfaces,
                GoPatternGroup.FunctionDeclarations => result.FunctionDeclarations,
                GoPatternGroup.MethodDeclarations => result.MethodDeclarations,
                GoPatternGroup.TypeDefinitions => result.TypeDefinitions,
                GoPatternGroup.ConstantSpecs => result.ConstantSpecs,
                GoPatternGroup.VariableSpecs => result.VariableSpecs,
                GoPatternGroup.Comments => result.Comments,
                GoPatternGroup.GoStatements => result.GoStatements,
                GoPatternGroup.ChannelTypes => result.ChannelTypes,
                GoPatternGroup.SelectStatements => result.SelectStatements,
                _ => throw new InvalidOperationException($"Unknown pattern group {group}")
            };
            bucket.Add(captures);
        }

        return result;
    }

    /// <summary>
    /// Holds pre-dispatched match lists from a single combined query execution.
    /// One list per <see cref="GoPatternGroup"/> — each element is a match (list of captures).
    /// </summary>
    private sealed class DispatchedMatches
    {
        public List<List<CaptureWithNode>> PackageClause { get; } = [];
        public List<List<CaptureWithNode>> ImportSpecs { get; } = [];
        public List<List<CaptureWithNode>> StructDeclarations { get; } = [];
        public List<List<CaptureWithNode>> StructFields { get; } = [];
        public List<List<CaptureWithNode>> InterfaceDeclarations { get; } = [];
        public List<List<CaptureWithNode>> InterfaceMethods { get; } = [];
        public List<List<CaptureWithNode>> EmbeddedInterfaces { get; } = [];
        public List<List<CaptureWithNode>> FunctionDeclarations { get; } = [];
        public List<List<CaptureWithNode>> MethodDeclarations { get; } = [];
        public List<List<CaptureWithNode>> TypeDefinitions { get; } = [];
        public List<List<CaptureWithNode>> ConstantSpecs { get; } = [];
        public List<List<CaptureWithNode>> VariableSpecs { get; } = [];
        public List<List<CaptureWithNode>> Comments { get; } = [];
        public List<List<CaptureWithNode>> GoStatements { get; } = [];
        public List<List<CaptureWithNode>> ChannelTypes { get; } = [];
        public List<List<CaptureWithNode>> SelectStatements { get; } = [];
    }
}
