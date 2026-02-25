using RepoQL.Formats.Python.Surface;
using TreeSitter;

namespace RepoQL.Formats.Python.TreeSitter;

public sealed class PythonTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public PythonDocumentSurface Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new PythonDocumentSurface(
                Classes: [],
                Functions: [],
                Imports: [],
                Constants: [],
                TypeAliases: [],
                AllExports: null,
                ModuleDocstring: null,
                MetaprogrammingHints: [],
                FrameworkHints: [],
                Stats: new PythonParseStats(0, 0, 0, 0, 0),
                ErrorNodeCount: 0);
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;

            var errorNodeCount = CountErrorNodes(root);
            var decoratorLookup = BuildDecoratorLookup(root);
            var classBuilders = BuildClasses(root, decoratorLookup);
            var classLookup = classBuilders.ToDictionary(c => GetNodeKey(c.Node), c => c, StringComparer.Ordinal);

            var functionMatches = ExecuteMatches(PythonQueries.FunctionDeclarations, root);
            var selfAssignments = ExecuteMatches(PythonQueries.SelfAttributeAssignments, root);
            var yieldNodes = ExecuteCaptures(PythonQueries.YieldSites, root).Select(c => c.Node).ToList();
            var asyncWithNodes = ExecuteCaptures(PythonQueries.AsyncWithSites, root).Select(c => c.Node).ToList();
            var asyncForNodes = ExecuteCaptures(PythonQueries.AsyncForSites, root).Select(c => c.Node).ToList();

            AttachMethods(functionMatches, classLookup, decoratorLookup, yieldNodes, asyncWithNodes, asyncForNodes);
            AttachClassVariablesAndSlots(classLookup.Values);
            AttachInstanceVariables(functionMatches, selfAssignments, classLookup);

            var functions = ExtractTopLevelFunctions(functionMatches, decoratorLookup, yieldNodes, asyncWithNodes, asyncForNodes);
            var imports = ExtractImports(root);
            var (constants, typeAliases, allExports, moduleDocstring) = ExtractModuleData(root);
            var metaprogrammingHints = ExtractMetaprogrammingHints(root, functionMatches);
            var frameworkHints = ExtractFrameworkHints(root);

            var classes = classBuilders
                .OrderBy(c => c.ByteRange.StartByte)
                .Select(c => c.ToSurface())
                .ToList();

            var lineCount = CountLines(sourceCode);
            var stats = new PythonParseStats(
                ClassCount: classes.Count,
                FunctionCount: functions.Count,
                ImportCount: imports.Count,
                LineCount: lineCount,
                ErrorNodeCount: errorNodeCount);

            return new PythonDocumentSurface(
                Classes: classes,
                Functions: functions,
                Imports: imports,
                Constants: constants,
                TypeAliases: typeAliases,
                AllExports: allExports,
                ModuleDocstring: moduleDocstring,
                MetaprogrammingHints: metaprogrammingHints,
                FrameworkHints: frameworkHints,
                Stats: stats,
                ErrorNodeCount: errorNodeCount);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native Python parser. Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    public IReadOnlyList<PythonQueryCaptureGroup> ExecuteQuery(string query, string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(sourceCode))
        {
            return [];
        }

        var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
        using var tree = parser.Parse(sourceCode);
        return ExecuteQuery(query, tree.RootNode);
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

    private static IReadOnlyList<PythonQueryCaptureGroup> ExecuteQuery(string query, Node rootNode)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        var groups = new List<PythonQueryCaptureGroup>();

        foreach (var match in cursor.Matches)
        {
            var captures = match.Captures
                .Where(c => !c.Node.IsError)
                .Select(c => new PythonQueryCapture(
                    c.Name,
                    c.Node.Text,
                    new PythonByteRange(c.Node.StartIndex, c.Node.EndIndex)))
                .ToList();

            if (captures.Count == 0)
            {
                continue;
            }

            groups.Add(new PythonQueryCaptureGroup(match.PatternIndex, captures));
        }

        return groups;
    }

    private static Dictionary<string, IReadOnlyList<PythonDecoratorInfo>> BuildDecoratorLookup(Node root)
    {
        var byDefinition = new Dictionary<string, List<(int Start, PythonDecoratorInfo Decorator)>>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(PythonQueries.DecoratedDefinitions, root))
        {
            var definitionNode = match.FirstOrDefault(c => c.Name == "definition").Node;
            var decoratorNode = match.FirstOrDefault(c => c.Name == "decorator").Node;
            if (IsNullNode(definitionNode) || IsNullNode(decoratorNode))
            {
                continue;
            }

            var key = GetNodeKey(definitionNode);
            if (!byDefinition.TryGetValue(key, out var decorators))
            {
                decorators = [];
                byDefinition[key] = decorators;
            }

            decorators.Add((
                decoratorNode.StartIndex,
                ParseDecorator(decoratorNode)));
        }

        return byDefinition.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<PythonDecoratorInfo>)pair.Value
                .OrderBy(v => v.Start)
                .Select(v => v.Decorator)
                .ToList(),
            StringComparer.Ordinal);
    }

    private static List<ClassBuilder> BuildClasses(
        Node root,
        IReadOnlyDictionary<string, IReadOnlyList<PythonDecoratorInfo>> decoratorLookup)
    {
        var classes = new List<ClassBuilder>();
        foreach (var match in ExecuteMatches(PythonQueries.ClassDeclarations, root))
        {
            var classNode = match.FirstOrDefault(c => c.Name == "class_node").Node;
            var classNameNode = match.FirstOrDefault(c => c.Name == "class_name").Node;
            var superclassesNode = match.FirstOrDefault(c => c.Name == "superclasses").Node;
            var classBodyNode = match.FirstOrDefault(c => c.Name == "class_body").Node;
            if (IsNullNode(classNode) || IsNullNode(classNameNode))
            {
                continue;
            }

            var name = NormalizeName(classNameNode.Text);
            var qualifiedName = BuildQualifiedClassName(classNode, name);
            var (bases, metaclass) = ParseSuperclasses(superclassesNode);
            var decorators = GetDecorators(decoratorLookup, classNode);
            var docstring = IsNullNode(classBodyNode)
                ? null
                : ExtractDocstringFromBlock(classBodyNode);

            classes.Add(new ClassBuilder(
                classNode,
                name,
                qualifiedName,
                bases,
                metaclass,
                decorators,
                docstring,
                new PythonByteRange(classNode.StartIndex, classNode.EndIndex)));
        }

        return classes;
    }
    private static void AttachMethods(
        IReadOnlyList<List<CaptureWithNode>> functionMatches,
        IReadOnlyDictionary<string, ClassBuilder> classLookup,
        IReadOnlyDictionary<string, IReadOnlyList<PythonDecoratorInfo>> decoratorLookup,
        IReadOnlyList<Node> yieldNodes,
        IReadOnlyList<Node> asyncWithNodes,
        IReadOnlyList<Node> asyncForNodes)
    {
        foreach (var match in functionMatches)
        {
            var functionNode = match.FirstOrDefault(c => c.Name == "function_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "function_name").Node;
            var parametersNode = match.FirstOrDefault(c => c.Name == "params").Node;
            var returnTypeNode = match.FirstOrDefault(c => c.Name == "return_type").Node;
            if (IsNullNode(functionNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var ownerNode = FindNearestScopeNode(functionNode);
            if (IsNullNode(ownerNode) || ownerNode!.Type != "class_definition")
            {
                continue;
            }

            if (!classLookup.TryGetValue(GetNodeKey(ownerNode), out var owner))
            {
                continue;
            }

            var methodName = NormalizeName(nameNode.Text);
            _ = DetermineVisibility(methodName);

            var isAsync = IsAsyncFunction(functionNode);
            var isGenerator = yieldNodes.Any(y => Contains(functionNode, y));
            var usesAsyncWith = asyncWithNodes.Any(n => Contains(functionNode, n));
            var usesAsyncFor = asyncForNodes.Any(n => Contains(functionNode, n));
            var parameters = IsNullNode(parametersNode)
                ? []
                : ExtractParameters(parametersNode);

            owner.Methods.Add(new PythonMethodInfo(
                Name: methodName,
                IsAsync: isAsync,
                IsGenerator: isGenerator,
                IsAsyncGenerator: isAsync && isGenerator,
                UsesAsyncWith: usesAsyncWith,
                UsesAsyncFor: usesAsyncFor,
                Decorators: GetDecorators(decoratorLookup, functionNode),
                Parameters: parameters,
                ReturnType: IsNullNode(returnTypeNode) ? null : NormalizeWhitespace(returnTypeNode.Text),
                Docstring: ExtractDocstringFromFunction(functionNode),
                ByteRange: new PythonByteRange(functionNode.StartIndex, functionNode.EndIndex)));
        }
    }

    private static void AttachClassVariablesAndSlots(IEnumerable<ClassBuilder> classes)
    {
        foreach (var klass in classes)
        {
            var bodyNode = TryGetField(klass.Node, "body");
            if (IsNullNode(bodyNode))
            {
                continue;
            }

            foreach (var statement in GetBodyStatements(bodyNode!))
            {
                var assignmentNode = GetAssignmentFromExpressionStatement(statement);
                if (IsNullNode(assignmentNode))
                {
                    continue;
                }

                var leftNode = TryGetField(assignmentNode!, "left");
                if (IsNullNode(leftNode) || leftNode!.Type != "identifier")
                {
                    continue;
                }

                var name = NormalizeName(leftNode.Text);
                _ = DetermineVisibility(name);

                var typeNode = TryGetField(assignmentNode!, "type");
                var rightNode = TryGetField(assignmentNode!, "right");
                var typeAnnotation = IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text);
                var valueText = IsNullNode(rightNode) ? null : NormalizeWhitespace(rightNode!.Text);

                if (name == "__slots__")
                {
                    klass.Slots = valueText;
                }

                klass.ClassVariables.Add(new PythonVariableInfo(
                    Name: name,
                    TypeAnnotation: typeAnnotation,
                    VariableKind: PythonVariableKind.Class,
                    ByteRange: new PythonByteRange(assignmentNode!.StartIndex, assignmentNode.EndIndex)));
            }
        }
    }

    private static void AttachInstanceVariables(
        IReadOnlyList<List<CaptureWithNode>> functionMatches,
        IReadOnlyList<List<CaptureWithNode>> selfAssignmentMatches,
        IReadOnlyDictionary<string, ClassBuilder> classLookup)
    {
        foreach (var match in functionMatches)
        {
            var functionNode = match.FirstOrDefault(c => c.Name == "function_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "function_name").Node;
            var parametersNode = match.FirstOrDefault(c => c.Name == "params").Node;
            if (IsNullNode(functionNode) || IsNullNode(nameNode) || NormalizeName(nameNode.Text) != "__init__")
            {
                continue;
            }

            var ownerNode = FindNearestScopeNode(functionNode);
            if (IsNullNode(ownerNode) || ownerNode!.Type != "class_definition")
            {
                continue;
            }

            if (!classLookup.TryGetValue(GetNodeKey(ownerNode), out var owner))
            {
                continue;
            }

            var parameters = IsNullNode(parametersNode)
                ? []
                : ExtractParameters(parametersNode);
            var parameterTypeLookup = parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.Type))
                .ToDictionary(p => p.Name, p => p.Type!, StringComparer.Ordinal);

            var vars = new List<PythonVariableInfo>();
            foreach (var assignmentMatch in selfAssignmentMatches)
            {
                var assignmentNode = assignmentMatch.FirstOrDefault(c => c.Name == "self_assignment").Node;
                var attributeNameNode = assignmentMatch.FirstOrDefault(c => c.Name == "attribute_name").Node;
                if (IsNullNode(assignmentNode) || IsNullNode(attributeNameNode) || !Contains(functionNode, assignmentNode))
                {
                    continue;
                }

                var name = NormalizeName(attributeNameNode.Text);
                _ = DetermineVisibility(name);

                var typeNode = TryGetField(assignmentNode, "type");
                var rightNode = TryGetField(assignmentNode, "right");
                var typeAnnotation = IsNullNode(typeNode)
                    ? null
                    : NormalizeWhitespace(typeNode!.Text);

                if (typeAnnotation is null)
                {
                    if (!IsNullNode(rightNode) && rightNode!.Type == "identifier"
                        && parameterTypeLookup.TryGetValue(NormalizeName(rightNode.Text), out var inferredFromRhs))
                    {
                        typeAnnotation = inferredFromRhs;
                    }
                    else if (parameterTypeLookup.TryGetValue(name, out var inferredByName))
                    {
                        typeAnnotation = inferredByName;
                    }
                }

                vars.Add(new PythonVariableInfo(
                    Name: name,
                    TypeAnnotation: typeAnnotation,
                    VariableKind: PythonVariableKind.Instance,
                    ByteRange: new PythonByteRange(assignmentNode.StartIndex, assignmentNode.EndIndex)));
            }

            foreach (var variable in vars
                         .GroupBy(v => v.Name, StringComparer.Ordinal)
                         .Select(g => g.OrderBy(v => v.ByteRange.StartByte).First())
                         .OrderBy(v => v.ByteRange.StartByte))
            {
                owner.InstanceVariables.Add(variable);
            }
        }
    }

    private static IReadOnlyList<PythonFunctionInfo> ExtractTopLevelFunctions(
        IReadOnlyList<List<CaptureWithNode>> functionMatches,
        IReadOnlyDictionary<string, IReadOnlyList<PythonDecoratorInfo>> decoratorLookup,
        IReadOnlyList<Node> yieldNodes,
        IReadOnlyList<Node> asyncWithNodes,
        IReadOnlyList<Node> asyncForNodes)
    {
        var functions = new List<PythonFunctionInfo>();
        foreach (var match in functionMatches)
        {
            var functionNode = match.FirstOrDefault(c => c.Name == "function_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "function_name").Node;
            var parametersNode = match.FirstOrDefault(c => c.Name == "params").Node;
            var returnTypeNode = match.FirstOrDefault(c => c.Name == "return_type").Node;
            if (IsNullNode(functionNode) || IsNullNode(nameNode))
            {
                continue;
            }

            if (!IsNullNode(FindNearestScopeNode(functionNode)))
            {
                continue;
            }

            var functionName = NormalizeName(nameNode.Text);
            _ = DetermineVisibility(functionName);

            var isAsync = IsAsyncFunction(functionNode);
            var isGenerator = yieldNodes.Any(y => Contains(functionNode, y));

            functions.Add(new PythonFunctionInfo(
                Name: functionName,
                IsAsync: isAsync,
                IsGenerator: isGenerator,
                IsAsyncGenerator: isAsync && isGenerator,
                UsesAsyncWith: asyncWithNodes.Any(n => Contains(functionNode, n)),
                UsesAsyncFor: asyncForNodes.Any(n => Contains(functionNode, n)),
                Decorators: GetDecorators(decoratorLookup, functionNode),
                Parameters: IsNullNode(parametersNode) ? [] : ExtractParameters(parametersNode),
                ReturnType: IsNullNode(returnTypeNode) ? null : NormalizeWhitespace(returnTypeNode.Text),
                Docstring: ExtractDocstringFromFunction(functionNode),
                ByteRange: new PythonByteRange(functionNode.StartIndex, functionNode.EndIndex)));
        }

        return functions
            .OrderBy(f => f.ByteRange.StartByte)
            .ToList();
    }
    private static IReadOnlyList<PythonImportInfo> ExtractImports(Node root)
    {
        var imports = new List<PythonImportInfo>();

        foreach (var match in ExecuteMatches(PythonQueries.ImportStatements, root))
        {
            var importNode = match.FirstOrDefault(c => c.Name == "import_statement").Node;
            var moduleNameNode = match.FirstOrDefault(c => c.Name == "module_name").Node;
            var aliasNode = match.FirstOrDefault(c => c.Name == "alias").Node;
            if (IsNullNode(importNode) || IsNullNode(moduleNameNode))
            {
                continue;
            }

            var module = NormalizeWhitespace(moduleNameNode.Text);
            var names = IsNullNode(aliasNode)
                ? Array.Empty<PythonImportName>()
                : [new PythonImportName(module, NormalizeName(aliasNode.Text))];

            imports.Add(new PythonImportInfo(
                Module: module,
                Names: names,
                IsRelative: false,
                RelativeLevel: 0,
                IsStar: false,
                IsTypeCheckingOnly: IsTypeCheckingImport(importNode),
                ByteRange: new PythonByteRange(importNode.StartIndex, importNode.EndIndex)));
        }

        var fromImportBuilders = new Dictionary<string, ImportFromBuilder>(StringComparer.Ordinal);
        foreach (var match in ExecuteMatches(PythonQueries.ImportFromStatements, root))
        {
            var importNode = match.FirstOrDefault(c => c.Name == "import_from_statement").Node;
            var moduleNameNode = match.FirstOrDefault(c => c.Name == "module_name").Node;
            var importNameNode = match.FirstOrDefault(c => c.Name == "import_name").Node;
            var importAliasNode = match.FirstOrDefault(c => c.Name == "import_alias").Node;
            var starNode = match.FirstOrDefault(c => c.Name == "star_import").Node;
            if (IsNullNode(importNode) || IsNullNode(moduleNameNode))
            {
                continue;
            }

            var moduleRaw = NormalizeWhitespace(moduleNameNode.Text);
            var relativeLevel = CountLeading(moduleRaw, '.');
            var isRelative = relativeLevel > 0 || moduleNameNode.Type == "relative_import";
            var module = isRelative ? moduleRaw.TrimStart('.') : moduleRaw;
            if (module.Length == 0)
            {
                module = null;
            }

            var key = GetNodeKey(importNode);
            if (!fromImportBuilders.TryGetValue(key, out var builder))
            {
                builder = new ImportFromBuilder(
                    module,
                    isRelative,
                    relativeLevel,
                    IsTypeCheckingImport(importNode),
                    new PythonByteRange(importNode.StartIndex, importNode.EndIndex));
                fromImportBuilders[key] = builder;
            }

            if (!IsNullNode(starNode))
            {
                builder.IsStar = true;
                continue;
            }

            if (IsNullNode(importNameNode))
            {
                continue;
            }

            builder.Names.Add(new PythonImportName(
                NormalizeWhitespace(importNameNode.Text),
                IsNullNode(importAliasNode) ? null : NormalizeName(importAliasNode.Text)));
        }

        imports.AddRange(fromImportBuilders.Values.Select(v => new PythonImportInfo(
            Module: v.Module,
            Names: v.Names,
            IsRelative: v.IsRelative,
            RelativeLevel: v.RelativeLevel,
            IsStar: v.IsStar,
            IsTypeCheckingOnly: v.IsTypeCheckingOnly,
            ByteRange: v.ByteRange)));

        return imports
            .OrderBy(i => i.ByteRange.StartByte)
            .ToList();
    }

    private static (IReadOnlyList<PythonConstantInfo> Constants, IReadOnlyList<PythonTypeAliasInfo> TypeAliases, string[]? AllExports, string? ModuleDocstring)
        ExtractModuleData(Node root)
    {
        var constants = new List<PythonConstantInfo>();
        var typeAliases = new List<PythonTypeAliasInfo>();
        string[]? allExports = null;

        foreach (var statement in GetBodyStatements(root))
        {
            var assignmentNode = GetAssignmentFromExpressionStatement(statement);
            if (IsNullNode(assignmentNode))
            {
                continue;
            }

            var nameNode = TryGetField(assignmentNode!, "left");
            if (IsNullNode(nameNode) || nameNode!.Type != "identifier")
            {
                continue;
            }

            var name = NormalizeName(nameNode.Text);
            var typeNode = TryGetField(assignmentNode, "type");
            var rightNode = TryGetField(assignmentNode, "right");
            var typeAnnotation = IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text);
            var valueText = IsNullNode(rightNode) ? null : NormalizeWhitespace(rightNode!.Text);

            if (name == "__all__")
            {
                allExports = ExtractAllExports(rightNode);
            }

            if (LooksLikeTypeAlias(typeAnnotation))
            {
                typeAliases.Add(new PythonTypeAliasInfo(
                    Name: name,
                    Definition: valueText,
                    ByteRange: new PythonByteRange(assignmentNode.StartIndex, assignmentNode.EndIndex)));
                continue;
            }

            constants.Add(new PythonConstantInfo(
                Name: name,
                TypeAnnotation: typeAnnotation,
                ValueText: valueText,
                IsFinal: IsFinalAnnotation(typeAnnotation),
                IsAllCaps: IsAllCapsName(name),
                ByteRange: new PythonByteRange(assignmentNode.StartIndex, assignmentNode.EndIndex)));
        }

        foreach (var match in ExecuteMatches(PythonQueries.TypeAliasStatements, root))
        {
            var aliasNode = match.FirstOrDefault(c => c.Name == "type_alias_statement").Node;
            var leftNode = match.FirstOrDefault(c => c.Name == "alias_left").Node;
            var rightNode = match.FirstOrDefault(c => c.Name == "alias_right").Node;
            if (IsNullNode(aliasNode) || IsNullNode(leftNode))
            {
                continue;
            }

            typeAliases.Add(new PythonTypeAliasInfo(
                Name: ExtractTypeAliasName(leftNode),
                Definition: IsNullNode(rightNode) ? null : NormalizeWhitespace(rightNode.Text),
                ByteRange: new PythonByteRange(aliasNode.StartIndex, aliasNode.EndIndex)));
        }

        var moduleDocstring = ExtractDocstringFromModule(root);

        return (
            constants.OrderBy(c => c.ByteRange.StartByte).ToList(),
            typeAliases
                .GroupBy(a => $"{a.Name}:{a.ByteRange.StartByte}:{a.ByteRange.EndByte}", StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(a => a.ByteRange.StartByte)
                .ToList(),
            allExports,
            moduleDocstring);
    }

    private static IReadOnlyList<PythonMetaprogrammingHint> ExtractMetaprogrammingHints(
        Node root,
        IReadOnlyList<List<CaptureWithNode>> functionMatches)
    {
        var hints = new List<PythonMetaprogrammingHint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in ExecuteMatches(PythonQueries.MetaprogrammingCalls, root))
        {
            var callNode = match.FirstOrDefault(c => c.Name == "meta_call").Node;
            var methodNode = match.FirstOrDefault(c => c.Name == "meta_function").Node;
            var argsNode = match.FirstOrDefault(c => c.Name == "arguments").Node;
            if (IsNullNode(callNode) || IsNullNode(methodNode))
            {
                continue;
            }

            var pattern = NormalizeName(methodNode.Text);
            if (pattern == "type" && CountCallArguments(argsNode) != 3)
            {
                continue;
            }

            pattern = pattern switch
            {
                "type" => "type_dynamic_class",
                "import_module" => "importlib.import_module",
                _ => pattern
            };

            AddMetaprogrammingHint(hints, seen, pattern, callNode, extractable: false);
        }

        foreach (var match in ExecuteMatches(PythonQueries.DunderDefinitions, root))
        {
            var methodNode = match.FirstOrDefault(c => c.Name == "method_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "method_name").Node;
            if (IsNullNode(methodNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var dunderName = NormalizeName(nameNode.Text);
            var owningClass = FindOwningClassNode(methodNode);
            if (owningClass is not null)
            {
                // Class-level __getattr__ — dynamic attribute access
                if (dunderName == "__getattr__")
                {
                    AddMetaprogrammingHint(hints, seen, "__getattr__", methodNode, extractable: false);
                }
            }
            else if (FindNearestScopeNode(methodNode) is null)
            {
                // Module-level __getattr__ or __dir__ (PEP 562)
                AddMetaprogrammingHint(hints, seen, dunderName + "_module", methodNode, extractable: false);
            }
        }

        foreach (var match in functionMatches)
        {
            var functionNode = match.FirstOrDefault(c => c.Name == "function_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "function_name").Node;
            if (IsNullNode(functionNode) || IsNullNode(nameNode) || IsNullNode(FindOwningClassNode(functionNode)))
            {
                continue;
            }

            var name = NormalizeName(nameNode.Text);
            if (name is "__new__" or "__init_subclass__")
            {
                AddMetaprogrammingHint(hints, seen, name, functionNode, extractable: false);
            }
        }

        return hints
            .OrderBy(h => h.ByteRange.StartByte)
            .ToList();
    }

    private static IReadOnlyList<PythonFrameworkHint> ExtractFrameworkHints(Node root)
    {
        var hints = new List<PythonFrameworkHint>();

        foreach (var match in ExecuteMatches(PythonQueries.FrameworkFieldPatterns, root))
        {
            var assignmentNode = match.FirstOrDefault(c => c.Name == "assignment_node").Node;
            var functionNode = match.FirstOrDefault(c => c.Name == "function_expr").Node;
            var callNode = match.FirstOrDefault(c => c.Name == "call_node").Node;
            if (IsNullNode(assignmentNode) || IsNullNode(functionNode) || IsNullNode(callNode))
            {
                continue;
            }

            var scopeNode = FindNearestScopeNode(assignmentNode);
            if (IsNullNode(scopeNode) || scopeNode!.Type != "class_definition")
            {
                continue;
            }

            var functionText = NormalizeWhitespace(functionNode.Text);
            var callText = NormalizeWhitespace(callNode.Text);

            string? ruleId = null;
            if (functionText.StartsWith("models.", StringComparison.Ordinal))
            {
                ruleId = "django_field";
            }
            else if (functionText.Equals("db.Column", StringComparison.Ordinal))
            {
                ruleId = "sqlalchemy_column";
            }
            else if (functionText.Equals("Field", StringComparison.Ordinal))
            {
                ruleId = "pydantic_field";
            }

            if (ruleId is null)
            {
                continue;
            }

            hints.Add(new PythonFrameworkHint(
                Kind: PythonConstants.AnnotationKinds.Framework,
                RuleId: ruleId,
                Message: callText,
                ByteRange: new PythonByteRange(assignmentNode.StartIndex, assignmentNode.EndIndex)));
        }

        return hints
            .GroupBy(h => $"{h.RuleId}:{h.ByteRange.StartByte}:{h.ByteRange.EndByte}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(h => h.ByteRange.StartByte)
            .ToList();
    }
    private static IReadOnlyList<PythonDecoratorInfo> GetDecorators(
        IReadOnlyDictionary<string, IReadOnlyList<PythonDecoratorInfo>> decoratorLookup,
        Node definitionNode)
    {
        return decoratorLookup.TryGetValue(GetNodeKey(definitionNode), out var decorators)
            ? decorators
            : [];
    }

    private static PythonDecoratorInfo ParseDecorator(Node decoratorNode)
    {
        var expressionNode = decoratorNode.NamedChildren.FirstOrDefault();
        if (IsNullNode(expressionNode))
        {
            return new PythonDecoratorInfo(string.Empty, null);
        }

        if (expressionNode!.Type == "call")
        {
            var functionNode = TryGetField(expressionNode, "function");
            var argumentsNode = TryGetField(expressionNode, "arguments");
            return new PythonDecoratorInfo(
                Name: IsNullNode(functionNode) ? NormalizeWhitespace(expressionNode.Text) : NormalizeWhitespace(functionNode!.Text),
                Arguments: IsNullNode(argumentsNode) ? null : NormalizeWhitespace(argumentsNode!.Text));
        }

        return new PythonDecoratorInfo(
            Name: NormalizeWhitespace(expressionNode.Text),
            Arguments: null);
    }

    private static IReadOnlyList<PythonParameterInfo> ExtractParameters(Node parametersNode)
    {
        var parameters = new List<PythonParameterInfo>();
        var keywordOnly = false;

        foreach (var child in parametersNode.NamedChildren)
        {
            switch (child.Type)
            {
                case "positional_separator":
                    for (var i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i].Kind == PythonParameterKind.PositionalOrKeyword)
                        {
                            parameters[i] = parameters[i] with { Kind = PythonParameterKind.PositionalOnly };
                        }
                    }
                    break;

                case "keyword_separator":
                    keywordOnly = true;
                    break;

                case "identifier":
                    parameters.Add(new PythonParameterInfo(
                        Name: NormalizeName(child.Text),
                        Type: null,
                        Default: null,
                        Kind: keywordOnly ? PythonParameterKind.KeywordOnly : PythonParameterKind.PositionalOrKeyword));
                    break;

                case "tuple_pattern":
                    parameters.Add(new PythonParameterInfo(
                        Name: NormalizeWhitespace(child.Text),
                        Type: null,
                        Default: null,
                        Kind: keywordOnly ? PythonParameterKind.KeywordOnly : PythonParameterKind.PositionalOrKeyword));
                    break;

                case "list_splat_pattern":
                    parameters.Add(new PythonParameterInfo(
                        Name: TrimSplatPrefix(child.Text),
                        Type: null,
                        Default: null,
                        Kind: PythonParameterKind.VarPositional));
                    keywordOnly = true;
                    break;

                case "dictionary_splat_pattern":
                    parameters.Add(new PythonParameterInfo(
                        Name: TrimSplatPrefix(child.Text),
                        Type: null,
                        Default: null,
                        Kind: PythonParameterKind.VarKeyword));
                    break;

                case "default_parameter":
                {
                    var nameNode = TryGetField(child, "name");
                    var valueNode = TryGetField(child, "value");
                    if (IsNullNode(nameNode))
                    {
                        break;
                    }

                    parameters.Add(new PythonParameterInfo(
                        Name: NormalizeWhitespace(nameNode!.Text),
                        Type: null,
                        Default: IsNullNode(valueNode) ? null : NormalizeWhitespace(valueNode!.Text),
                        Kind: keywordOnly ? PythonParameterKind.KeywordOnly : PythonParameterKind.PositionalOrKeyword));
                    break;
                }

                case "typed_default_parameter":
                {
                    var nameNode = TryGetField(child, "name");
                    var typeNode = TryGetField(child, "type");
                    var valueNode = TryGetField(child, "value");
                    if (IsNullNode(nameNode))
                    {
                        break;
                    }

                    parameters.Add(new PythonParameterInfo(
                        Name: NormalizeName(nameNode!.Text),
                        Type: IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text),
                        Default: IsNullNode(valueNode) ? null : NormalizeWhitespace(valueNode!.Text),
                        Kind: keywordOnly ? PythonParameterKind.KeywordOnly : PythonParameterKind.PositionalOrKeyword));
                    break;
                }

                case "typed_parameter":
                {
                    var typeNode = TryGetField(child, "type");
                    var targetNode = child.NamedChildren.FirstOrDefault(c => c.Type is "identifier" or "list_splat_pattern" or "dictionary_splat_pattern");
                    if (IsNullNode(targetNode))
                    {
                        break;
                    }

                    if (targetNode!.Type == "list_splat_pattern")
                    {
                        parameters.Add(new PythonParameterInfo(
                            Name: TrimSplatPrefix(targetNode.Text),
                            Type: IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text),
                            Default: null,
                            Kind: PythonParameterKind.VarPositional));
                        keywordOnly = true;
                    }
                    else if (targetNode.Type == "dictionary_splat_pattern")
                    {
                        parameters.Add(new PythonParameterInfo(
                            Name: TrimSplatPrefix(targetNode.Text),
                            Type: IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text),
                            Default: null,
                            Kind: PythonParameterKind.VarKeyword));
                    }
                    else
                    {
                        parameters.Add(new PythonParameterInfo(
                            Name: NormalizeName(targetNode.Text),
                            Type: IsNullNode(typeNode) ? null : NormalizeWhitespace(typeNode!.Text),
                            Default: null,
                            Kind: keywordOnly ? PythonParameterKind.KeywordOnly : PythonParameterKind.PositionalOrKeyword));
                    }

                    break;
                }
            }
        }

        return parameters;
    }

    private static (IReadOnlyList<string> BaseClasses, string? Metaclass) ParseSuperclasses(Node? superclassesNode)
    {
        if (IsNullNode(superclassesNode))
        {
            return ([], null);
        }

        var baseClasses = new List<string>();
        string? metaclass = null;

        foreach (var child in superclassesNode!.NamedChildren)
        {
            if (child.Type == "keyword_argument")
            {
                var nameNode = TryGetField(child, "name");
                var valueNode = TryGetField(child, "value");
                if (!IsNullNode(nameNode) && NormalizeName(nameNode!.Text) == "metaclass" && !IsNullNode(valueNode))
                {
                    metaclass = NormalizeWhitespace(valueNode!.Text);
                }

                continue;
            }

            baseClasses.Add(NormalizeWhitespace(child.Text));
        }

        return (baseClasses, metaclass);
    }

    private static string? ExtractDocstringFromFunction(Node functionNode)
    {
        var bodyNode = TryGetField(functionNode, "body");
        return IsNullNode(bodyNode)
            ? null
            : ExtractDocstringFromBlock(bodyNode!);
    }

    private static string? ExtractDocstringFromModule(Node root)
    {
        return ExtractDocstringFromBlock(root);
    }

    private static string? ExtractDocstringFromBlock(Node blockNode)
    {
        foreach (var statement in GetBodyStatements(blockNode))
        {
            if (statement.Type == "string")
            {
                return UnquoteString(statement.Text);
            }

            if (statement.Type != "expression_statement")
            {
                return null;
            }

            var firstChild = statement.NamedChildren.FirstOrDefault();
            if (!IsNullNode(firstChild) && firstChild!.Type == "string")
            {
                return UnquoteString(firstChild.Text);
            }

            return null;
        }

        return null;
    }

    private static IEnumerable<Node> GetBodyStatements(Node node)
    {
        foreach (var child in node.NamedChildren.OrderBy(c => c.StartIndex))
        {
            foreach (var statement in UnwrapStatement(child))
            {
                yield return statement;
            }
        }
    }

    private static IEnumerable<Node> UnwrapStatement(Node node)
    {
        if (node.Type.StartsWith("_", StringComparison.Ordinal) && node.NamedChildren.Any())
        {
            foreach (var child in node.NamedChildren)
            {
                foreach (var statement in UnwrapStatement(child))
                {
                    yield return statement;
                }
            }

            yield break;
        }

        yield return node;
    }

    private static string[]? ExtractAllExports(Node? rightNode)
    {
        if (IsNullNode(rightNode))
        {
            return null;
        }

        var values = new List<string>();
        CollectStringValues(rightNode!, values);

        return values.Count == 0
            ? null
            : values.ToArray();
    }

    private static void CollectStringValues(Node node, ICollection<string> values)
    {
        if (node.Type == "string")
        {
            values.Add(UnquoteString(node.Text));
            return;
        }

        foreach (var child in node.NamedChildren)
        {
            CollectStringValues(child, values);
        }
    }

    private static void AddMetaprogrammingHint(
        ICollection<PythonMetaprogrammingHint> hints,
        ISet<string> seen,
        string pattern,
        Node node,
        bool extractable)
    {
        var key = $"{pattern}:{node.StartIndex}:{node.EndIndex}";
        if (!seen.Add(key))
        {
            return;
        }

        hints.Add(new PythonMetaprogrammingHint(
            PatternName: pattern,
            ByteRange: new PythonByteRange(node.StartIndex, node.EndIndex),
            Extractable: extractable));
    }

    private static int CountCallArguments(Node? argsNode)
    {
        if (IsNullNode(argsNode))
        {
            return 0;
        }

        if (argsNode!.Type == "generator_expression")
        {
            return 1;
        }

        return argsNode.NamedChildren.Count(c => c.Type is not ",");
    }

    private static bool IsTypeCheckingImport(Node importNode)
    {
        Node? current = importNode.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type == "if_statement")
            {
                var conditionNode = TryGetField(current, "condition");
                if (!IsNullNode(conditionNode)
                    && conditionNode!.Text.Contains("TYPE_CHECKING", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            current = current.Parent;
        }

        return false;
    }

    internal static string DetermineVisibility(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "public";
        }

        if (name.StartsWith("__", StringComparison.Ordinal)
            && name.EndsWith("__", StringComparison.Ordinal)
            && name.Length > 4)
        {
            return "public";
        }

        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            return "private";
        }

        return name.StartsWith("_", StringComparison.Ordinal)
            ? "private"
            : "public";
    }

    private static bool IsAsyncFunction(Node functionNode)
        => functionNode.Text.TrimStart().StartsWith("async def", StringComparison.Ordinal);

    private static Node? FindOwningClassNode(Node node)
    {
        Node? current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type == "class_definition")
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Node? FindNearestScopeNode(Node node)
    {
        Node? current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type is "class_definition" or "function_definition")
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Node? GetAssignmentFromExpressionStatement(Node statementNode)
    {
        if (statementNode.Type == "assignment")
        {
            return statementNode;
        }

        if (statementNode.Type != "expression_statement")
        {
            return null;
        }

        return statementNode.NamedChildren.FirstOrDefault(c => c.Type == "assignment");
    }
    private static string BuildQualifiedClassName(Node classNode, string localName)
    {
        var segments = new Stack<string>();
        segments.Push(localName);

        Node? current = classNode.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type == "class_definition")
            {
                var nameNode = TryGetField(current, "name");
                if (!IsNullNode(nameNode))
                {
                    segments.Push(NormalizeName(nameNode!.Text));
                }
            }

            current = current.Parent;
        }

        return string.Join('.', segments);
    }

    private static string ExtractTypeAliasName(Node leftNode)
    {
        var identifier = FindFirstDescendant(leftNode, static n => n.Type == "identifier");
        if (!IsNullNode(identifier))
        {
            return NormalizeName(identifier!.Text);
        }

        var text = NormalizeWhitespace(leftNode.Text);
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        return bracket >= 0 ? text[..bracket] : text;
    }

    private static Node? FindFirstDescendant(Node node, Func<Node, bool> predicate)
    {
        if (predicate(node))
        {
            return node;
        }

        foreach (var child in node.NamedChildren)
        {
            var result = FindFirstDescendant(child, predicate);
            if (!IsNullNode(result))
            {
                return result;
            }
        }

        return null;
    }

    private static bool IsFinalAnnotation(string? typeAnnotation)
        => !string.IsNullOrWhiteSpace(typeAnnotation)
           && typeAnnotation.Contains("Final", StringComparison.Ordinal);

    private static bool LooksLikeTypeAlias(string? typeAnnotation)
        => !string.IsNullOrWhiteSpace(typeAnnotation)
           && typeAnnotation.Contains("TypeAlias", StringComparison.Ordinal);

    private static bool IsAllCapsName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var hasLetter = false;
        foreach (var ch in name)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                if (!char.IsUpper(ch))
                {
                    return false;
                }

                continue;
            }

            if (char.IsDigit(ch) || ch == '_')
            {
                continue;
            }

            return false;
        }

        return hasLetter;
    }

    private static string TrimSplatPrefix(string text)
    {
        var trimmed = NormalizeName(text).Trim();
        while (trimmed.StartsWith('*'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static int CountLeading(string text, char target)
    {
        var count = 0;
        while (count < text.Length && text[count] == target)
        {
            count++;
        }

        return count;
    }

    private static string UnquoteString(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var value = text.Trim();

        var startQuote = value.IndexOfAny(['\'', '"']);
        if (startQuote > 0)
        {
            value = value[startQuote..];
        }

        if (value.StartsWith("\"\"\"", StringComparison.Ordinal)
            && value.EndsWith("\"\"\"", StringComparison.Ordinal)
            && value.Length >= 6)
        {
            return value[3..^3];
        }

        if (value.StartsWith("'''", StringComparison.Ordinal)
            && value.EndsWith("'''", StringComparison.Ordinal)
            && value.Length >= 6)
        {
            return value[3..^3];
        }

        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
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

    private static bool Contains(Node outer, Node inner)
        => outer.StartIndex <= inner.StartIndex && outer.EndIndex >= inner.EndIndex;

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

        var lines = 1;
        for (var i = 0; i < sourceCode.Length; i++)
        {
            if (sourceCode[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static bool IsNullNode(Node? node)
        => node is null || node.Id == IntPtr.Zero;

    private static string NormalizeName(string text)
        => text.Trim().Trim('"', '\'');

    private static string NormalizeWhitespace(string text)
        => string.Join(' ', text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static string GetNodeKey(Node node)
        => $"{node.StartIndex}:{node.EndIndex}:{node.Type}";

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-python", "tree_sitter_python");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter Python grammar from TreeSitter.DotNet. Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PythonTreeSitterClient));
        }
    }

    private readonly record struct CaptureWithNode(string Name, Node Node);

    private sealed class ImportFromBuilder(
        string? module,
        bool isRelative,
        int relativeLevel,
        bool isTypeCheckingOnly,
        PythonByteRange byteRange)
    {
        public string? Module { get; } = module;
        public bool IsRelative { get; } = isRelative;
        public int RelativeLevel { get; } = relativeLevel;
        public bool IsTypeCheckingOnly { get; } = isTypeCheckingOnly;
        public PythonByteRange ByteRange { get; } = byteRange;
        public List<PythonImportName> Names { get; } = [];
        public bool IsStar { get; set; }
    }

    private sealed class ClassBuilder(
        Node node,
        string name,
        string qualifiedName,
        IReadOnlyList<string> baseClasses,
        string? metaclass,
        IReadOnlyList<PythonDecoratorInfo> decorators,
        string? docstring,
        PythonByteRange byteRange)
    {
        public Node Node { get; } = node;
        public string Name { get; } = name;
        public string QualifiedName { get; } = qualifiedName;
        public IReadOnlyList<string> BaseClasses { get; } = baseClasses;
        public string? Metaclass { get; } = metaclass;
        public IReadOnlyList<PythonDecoratorInfo> Decorators { get; } = decorators;
        public string? Docstring { get; } = docstring;
        public string? Slots { get; set; }
        public PythonByteRange ByteRange { get; } = byteRange;
        public List<PythonMethodInfo> Methods { get; } = [];
        public List<PythonVariableInfo> ClassVariables { get; } = [];
        public List<PythonVariableInfo> InstanceVariables { get; } = [];

        public PythonClassInfo ToSurface()
            => new(
                Name: Name,
                QualifiedName: QualifiedName,
                BaseClasses: BaseClasses,
                Metaclass: Metaclass,
                Decorators: Decorators,
                Methods: Methods.OrderBy(m => m.ByteRange.StartByte).ToList(),
                ClassVariables: ClassVariables.OrderBy(v => v.ByteRange.StartByte).ToList(),
                InstanceVariables: InstanceVariables.OrderBy(v => v.ByteRange.StartByte).ToList(),
                Slots: Slots,
                Docstring: Docstring,
                ByteRange: ByteRange);
    }
}
