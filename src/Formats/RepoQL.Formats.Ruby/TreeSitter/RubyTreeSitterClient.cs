using RepoQL.Formats.Ruby.Surface;
using TreeSitter;

namespace RepoQL.Formats.Ruby.TreeSitter;

public sealed class RubyTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private static readonly Query SharedCombinedQuery = SharedLanguage.CreateQuery(RubyQueries.CombinedQuery);
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public RubyDocumentSurface Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new RubyDocumentSurface(
                Classes: [],
                Modules: [],
                Functions: [],
                Requires: [],
                Aliases: [],
                MetaprogrammingHints: [],
                Stats: new RubyParseStats(0, 0, 0, 0),
                ErrorNodeCount: 0);
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;

            var errorNodeCount = CountErrorNodes(root);
            var dispatched = ExecuteCombinedQuery(root);

            var classBuilders = BuildClasses(dispatched.ClassDeclarations);
            var moduleBuilders = BuildModules(dispatched.ModuleDeclarations);

            var ownerLookup = BuildOwnerLookup(classBuilders, moduleBuilders);

            ApplyVisibilities(dispatched.VisibilityBare, dispatched.VisibilityTargeted, ownerLookup);
            AttachMethods(dispatched.MethodDeclarations, dispatched.YieldSites, dispatched.BlockParameters, ownerLookup);
            AttachSingletonMethods(dispatched.SingletonMethods, ownerLookup);
            AttachMixins(dispatched.Mixins, ownerLookup);
            AttachConstants(dispatched.Constants, ownerLookup);
            AttachAttributes(dispatched.AttributeAccessors, ownerLookup);

            var (functions, totalMethodCount) = ExtractTopLevelFunctions(dispatched.MethodDeclarations, ownerLookup);
            var requires = ExtractRequires(dispatched.RequireStatements);
            var aliases = ExtractAliases(dispatched.AliasStatements, dispatched.AliasMethodCalls);
            var metaprogrammingHints = ExtractMetaprogrammingHints(dispatched.MetaprogrammingCalls, dispatched.MethodMissingDefinitions);

            var classes = classBuilders
                .OrderBy(b => b.ByteRange.StartByte)
                .Select(b => b.ToSurface())
                .ToList();
            var modules = moduleBuilders
                .OrderBy(b => b.ByteRange.StartByte)
                .Select(b => b.ToSurface())
                .ToList();

            var lineCount = CountLines(sourceCode);

            return new RubyDocumentSurface(
                Classes: classes,
                Modules: modules,
                Functions: functions,
                Requires: requires,
                Aliases: aliases,
                MetaprogrammingHints: metaprogrammingHints,
                Stats: new RubyParseStats(classes.Count, modules.Count, totalMethodCount + functions.Count, lineCount),
                ErrorNodeCount: errorNodeCount);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native Ruby parser. Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    public IReadOnlyList<RubyQueryCaptureGroup> ExecuteQuery(string query, string sourceCode)
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

    private static IReadOnlyList<RubyQueryCaptureGroup> ExecuteQuery(string query, Node rootNode)
    {
        using var treeSitterQuery = SharedLanguage.CreateQuery(query);
        using var cursor = treeSitterQuery.Execute(rootNode);
        var groups = new List<RubyQueryCaptureGroup>();

        foreach (var match in cursor.Matches)
        {
            var captures = match.Captures
                .Where(c => !c.Node.IsError)
                .Select(c => new RubyQueryCapture(
                    c.Name,
                    c.Node.Text,
                    new RubyByteRange(c.Node.StartIndex, c.Node.EndIndex)))
                .ToList();

            if (captures.Count == 0)
            {
                continue;
            }

            groups.Add(new RubyQueryCaptureGroup(match.PatternIndex, captures));
        }

        return groups;
    }

    private static List<ClassBuilder> BuildClasses(List<List<CaptureWithNode>> matches)
    {
        var captures = matches.SelectMany(m => m).ToList();
        var byClassNode = captures
            .Where(c => c.Name == "class_name")
            .GroupBy(c => GetOwnerNode(c.Node, "class"))
            .Where(g => !IsNullNode(g.Key))
            .ToDictionary(g => g.Key!, g => g.First().Node);

        var supers = captures
            .Where(c => c.Name == "super")
            .GroupBy(c => GetOwnerNode(c.Node, "class"))
            .Where(g => !IsNullNode(g.Key))
            .ToDictionary(g => g.Key!, g => g.First().Node);

        var classes = new List<ClassBuilder>();
        foreach (var pair in byClassNode)
        {
            var classNode = pair.Key;
            var classNameNode = pair.Value;
            var name = NormalizeName(classNameNode.Text);
            var qualifiedName = BuildQualifiedName(classNode, name);
            var superclass = supers.TryGetValue(classNode, out var superNode)
                ? NormalizeName(superNode.Text)
                : TryGetSuperclass(classNode);

            classes.Add(new ClassBuilder(
                classNode,
                name,
                qualifiedName,
                superclass,
                superclass is not null,
                new RubyByteRange(classNode.StartIndex, classNode.EndIndex)));
        }

        return classes;
    }

    private static List<ModuleBuilder> BuildModules(List<List<CaptureWithNode>> matches)
    {
        var captures = matches.SelectMany(m => m).ToList();
        var byModuleNode = captures
            .Where(c => c.Name == "module_name")
            .GroupBy(c => GetOwnerNode(c.Node, "module"))
            .Where(g => !IsNullNode(g.Key))
            .ToDictionary(g => g.Key!, g => g.First().Node);

        var modules = new List<ModuleBuilder>();
        foreach (var pair in byModuleNode)
        {
            var moduleNode = pair.Key;
            var moduleNameNode = pair.Value;
            var name = NormalizeName(moduleNameNode.Text);
            var qualifiedName = BuildQualifiedName(moduleNode, name);

            modules.Add(new ModuleBuilder(
                moduleNode,
                name,
                qualifiedName,
                CountNestingDepth(moduleNode),
                new RubyByteRange(moduleNode.StartIndex, moduleNode.EndIndex)));
        }

        return modules;
    }

    private static Dictionary<string, OwnerBuilder> BuildOwnerLookup(
        IReadOnlyList<ClassBuilder> classes,
        IReadOnlyList<ModuleBuilder> modules)
    {
        var map = new Dictionary<string, OwnerBuilder>(StringComparer.Ordinal);
        foreach (var c in classes)
        {
            map[GetNodeKey(c.Node)] = c;
        }

        foreach (var m in modules)
        {
            map[GetNodeKey(m.Node)] = m;
        }

        return map;
    }

    private static void ApplyVisibilities(
        List<List<CaptureWithNode>> visibilityBare,
        List<List<CaptureWithNode>> visibilityTargeted,
        IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        foreach (var owner in ownerLookup.Values)
        {
            owner.VisibilityTransitions.Clear();
            owner.TargetedVisibilities.Clear();
        }

        var bareVisibilityCaptures = visibilityBare.SelectMany(m => m)
            .Where(c => c.Name == "vis")
            .Where(c => IsVisibility(c.Node.Text))
            .Where(c => c.Node.Parent?.Type == "body_statement")
            .Where(c => c.Node.Parent?.Parent?.Type is "class" or "module");

        foreach (var capture in bareVisibilityCaptures)
        {
            var ownerNode = capture.Node.Parent?.Parent;
            if (IsNullNode(ownerNode) || !ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            owner.VisibilityTransitions.Add((capture.Node.StartIndex, NormalizeName(capture.Node.Text)));
        }

        var targetedVisibilityCaptures = visibilityTargeted;
        foreach (var match in targetedVisibilityCaptures)
        {
            var visCapture = match.FirstOrDefault(c => c.Name == "vis");
            var targetCapture = match.FirstOrDefault(c => c.Name == "target");
            var callCapture = match.FirstOrDefault(c => c.Name == "visibility_call");
            if (visCapture.Node.Equals(default(Node)) || targetCapture.Node.Equals(default(Node)) || callCapture.Node.Equals(default(Node)))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(callCapture.Node);
            if (IsNullNode(ownerNode) || !ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            var targetName = NormalizeSymbolName(targetCapture.Node.Text);
            owner.TargetedVisibilities[targetName] = NormalizeName(visCapture.Node.Text);
        }
    }

    private static void AttachMethods(
        List<List<CaptureWithNode>> methodDeclarations,
        List<List<CaptureWithNode>> yieldSites,
        List<List<CaptureWithNode>> blockParameters,
        IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        var methodCaptures = methodDeclarations;
        var yields = yieldSites.SelectMany(m => m).Select(c => c.Node).ToList();
        var blockParams = blockParameters.SelectMany(m => m).Select(c => c.Node).ToList();

        foreach (var match in methodCaptures)
        {
            var methodNode = match.FirstOrDefault(c => c.Name == "method_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "method_name").Node;
            var paramsNode = match.FirstOrDefault(c => c.Name == "params").Node;
            if (IsNullNode(methodNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(methodNode);
            if (IsNullNode(ownerNode))
            {
                continue;
            }

            if (!ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            var methodName = NormalizeName(nameNode.Text);
            var parameterText = IsNullNode(paramsNode) ? null : NormalizeWhitespace(paramsNode.Text);
            var visibility = owner.ResolveVisibility(methodName, methodNode.StartIndex);
            var acceptsBlock = yields.Any(y => Contains(methodNode, y)) || blockParams.Any(b => Contains(methodNode, b));
            var isInsideSingletonClass = IsInsideSingletonClass(methodNode, ownerNode);
            var methodInfo = new RubyMethodInfo(
                methodName,
                visibility,
                isInsideSingletonClass,
                parameterText,
                acceptsBlock,
                new RubyByteRange(methodNode.StartIndex, methodNode.EndIndex));

            if (isInsideSingletonClass && owner is ClassBuilder classOwner)
            {
                classOwner.SingletonMethods.Add(new RubySingletonMethodInfo(
                    methodName,
                    "self",
                    parameterText,
                    new RubyByteRange(methodNode.StartIndex, methodNode.EndIndex)));
            }
            else
            {
                owner.Methods.Add(methodInfo);
            }
        }
    }

    private static void AttachSingletonMethods(List<List<CaptureWithNode>> matches, IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        foreach (var match in matches)
        {
            var singletonMethodNode = match.FirstOrDefault(c => c.Name == "singleton_method").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "method_name").Node;
            var receiverNode = match.FirstOrDefault(c => c.Name == "receiver").Node;
            var paramsNode = match.FirstOrDefault(c => c.Name == "params").Node;
            if (IsNullNode(singletonMethodNode) || IsNullNode(nameNode) || IsNullNode(receiverNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(singletonMethodNode);
            if (IsNullNode(ownerNode) || !ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            if (owner is not ClassBuilder classOwner)
            {
                continue;
            }

            classOwner.SingletonMethods.Add(new RubySingletonMethodInfo(
                NormalizeName(nameNode.Text),
                NormalizeName(receiverNode.Text),
                IsNullNode(paramsNode) ? null : NormalizeWhitespace(paramsNode.Text),
                new RubyByteRange(singletonMethodNode.StartIndex, singletonMethodNode.EndIndex)));
        }
    }

    private static void AttachMixins(List<List<CaptureWithNode>> mixinMatches, IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        var matches = mixinMatches
            .OrderBy(m =>
            {
                var node = m.FirstOrDefault(c => c.Name == "mixin_call").Node;
                return IsNullNode(node) ? int.MaxValue : node.StartIndex;
            });

        var ordinalByOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            var mixinCallNode = match.FirstOrDefault(c => c.Name == "mixin_call").Node;
            var typeNode = match.FirstOrDefault(c => c.Name == "mixin_type").Node;
            var moduleNode = match.FirstOrDefault(c => c.Name == "module").Node;
            if (IsNullNode(mixinCallNode) || IsNullNode(typeNode) || IsNullNode(moduleNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(mixinCallNode);
            if (IsNullNode(ownerNode))
            {
                continue;
            }

            var ownerKey = GetNodeKey(ownerNode!);
            if (!ownerLookup.TryGetValue(ownerKey, out var owner))
            {
                continue;
            }

            var ordinal = ordinalByOwner.TryGetValue(ownerKey, out var existing)
                ? existing + 1
                : 0;
            ordinalByOwner[ownerKey] = ordinal;

            owner.Mixins.Add(new RubyMixinInfo(
                NormalizeName(moduleNode.Text),
                NormalizeName(typeNode.Text),
                ordinal));
        }
    }

    private static void AttachConstants(List<List<CaptureWithNode>> matches, IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        foreach (var match in matches)
        {
            var constNode = match.FirstOrDefault(c => c.Name == "const_name").Node;
            if (IsNullNode(constNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(constNode);
            if (IsNullNode(ownerNode) || !ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            owner.Constants.Add(new RubyConstantInfo(
                NormalizeName(constNode.Text),
                new RubyByteRange(constNode.StartIndex, constNode.EndIndex)));
        }
    }

    private static void AttachAttributes(List<List<CaptureWithNode>> matches, IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        foreach (var match in matches)
        {
            var callNode = match.FirstOrDefault(c => c.Name == "attribute_call").Node;
            var callNameNode = match.FirstOrDefault(c => c.Name == "call_name").Node;
            var argsNode = match.FirstOrDefault(c => c.Name == "args").Node;
            if (IsNullNode(callNode) || IsNullNode(callNameNode) || IsNullNode(argsNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(callNode);
            if (IsNullNode(ownerNode) || !ownerLookup.TryGetValue(GetNodeKey(ownerNode!), out var owner))
            {
                continue;
            }

            var accessorType = NormalizeName(callNameNode.Text).Replace("attr_", string.Empty, StringComparison.Ordinal);
            var visibility = owner.ResolveVisibilityAt(callNode.StartIndex);
            var symbols = argsNode.NamedChildren
                .Where(n => n.Type == "simple_symbol")
                .Select(n => NormalizeSymbolName(n.Text))
                .Where(n => !string.IsNullOrWhiteSpace(n));

            foreach (var symbol in symbols)
            {
                owner.Attributes.Add(new RubyAttributeInfo(
                    symbol,
                    accessorType,
                    visibility,
                    new RubyByteRange(callNode.StartIndex, callNode.EndIndex)));
            }
        }
    }

    private static (IReadOnlyList<RubyMethodInfo> Functions, int MethodCountInOwners) ExtractTopLevelFunctions(
        List<List<CaptureWithNode>> methodDeclarations,
        IReadOnlyDictionary<string, OwnerBuilder> ownerLookup)
    {
        var methodsInOwners = ownerLookup.Values.Sum(o => o.Methods.Count + (o is ClassBuilder c ? c.SingletonMethods.Count : 0));
        var functions = new List<RubyMethodInfo>();
        var methodMatches = methodDeclarations;
        foreach (var match in methodMatches)
        {
            var methodNode = match.FirstOrDefault(c => c.Name == "method_node").Node;
            var nameNode = match.FirstOrDefault(c => c.Name == "method_name").Node;
            var paramsNode = match.FirstOrDefault(c => c.Name == "params").Node;
            if (IsNullNode(methodNode) || IsNullNode(nameNode))
            {
                continue;
            }

            var ownerNode = FindOwningTypeNode(methodNode);
            if (!IsNullNode(ownerNode))
            {
                continue;
            }

            functions.Add(new RubyMethodInfo(
                NormalizeName(nameNode.Text),
                "public",
                false,
                IsNullNode(paramsNode) ? null : NormalizeWhitespace(paramsNode.Text),
                methodNode.Text.Contains("yield", StringComparison.Ordinal),
                new RubyByteRange(methodNode.StartIndex, methodNode.EndIndex)));
        }

        return (functions, methodsInOwners);
    }

    private static IReadOnlyList<RubyRequireInfo> ExtractRequires(List<List<CaptureWithNode>> matches)
    {
        var requires = new List<RubyRequireInfo>();
        foreach (var match in matches)
        {
            var reqMethodNode = match.FirstOrDefault(c => c.Name == "req_method").Node;
            var pathNode = match.FirstOrDefault(c => c.Name == "path").Node;
            var callNode = match.FirstOrDefault(c => c.Name == "require_call").Node;
            if (IsNullNode(reqMethodNode) || IsNullNode(pathNode) || IsNullNode(callNode))
            {
                continue;
            }

            var methodName = NormalizeName(reqMethodNode.Text);
            requires.Add(new RubyRequireInfo(
                pathNode.Text,
                methodName.Equals("require_relative", StringComparison.Ordinal),
                new RubyByteRange(callNode.StartIndex, callNode.EndIndex)));
        }

        return requires;
    }

    private static IReadOnlyList<RubyAliasInfo> ExtractAliases(
        List<List<CaptureWithNode>> aliasStatements,
        List<List<CaptureWithNode>> aliasMethodCalls)
    {
        var aliases = new List<RubyAliasInfo>();

        foreach (var match in aliasStatements)
        {
            var aliasNode = match.FirstOrDefault(c => c.Name == "alias_node").Node;
            var newNameNode = match.FirstOrDefault(c => c.Name == "new_name").Node;
            var originalNameNode = match.FirstOrDefault(c => c.Name == "original_name").Node;
            if (IsNullNode(aliasNode) || IsNullNode(newNameNode) || IsNullNode(originalNameNode))
            {
                continue;
            }

            aliases.Add(new RubyAliasInfo(
                NormalizeSymbolName(newNameNode.Text),
                NormalizeSymbolName(originalNameNode.Text),
                "alias",
                new RubyByteRange(aliasNode.StartIndex, aliasNode.EndIndex)));
        }

        foreach (var match in aliasMethodCalls)
        {
            var aliasCallNode = match.FirstOrDefault(c => c.Name == "alias_call").Node;
            var newNameNode = match.FirstOrDefault(c => c.Name == "new_name").Node;
            var originalNameNode = match.FirstOrDefault(c => c.Name == "original_name").Node;
            if (IsNullNode(aliasCallNode) || IsNullNode(newNameNode) || IsNullNode(originalNameNode))
            {
                continue;
            }

            aliases.Add(new RubyAliasInfo(
                NormalizeSymbolName(newNameNode.Text),
                NormalizeSymbolName(originalNameNode.Text),
                "alias_method",
                new RubyByteRange(aliasCallNode.StartIndex, aliasCallNode.EndIndex)));
        }

        return aliases;
    }

    private static IReadOnlyList<RubyMetaprogrammingHint> ExtractMetaprogrammingHints(
        List<List<CaptureWithNode>> metaprogrammingCalls,
        List<List<CaptureWithNode>> methodMissingDefinitions)
    {
        var hints = new List<RubyMetaprogrammingHint>();
        var matches = metaprogrammingCalls;
        foreach (var match in matches)
        {
            var metaMethodNode = match.FirstOrDefault(c => c.Name == "meta_method").Node;
            var metaCallNode = match.FirstOrDefault(c => c.Name == "meta_call").Node;
            if (IsNullNode(metaMethodNode) || IsNullNode(metaCallNode))
            {
                continue;
            }

            var pattern = NormalizeName(metaMethodNode.Text);
            var argsNode = match.FirstOrDefault(c => c.Name == "args").Node;
            var extractable = pattern.Equals("define_method", StringComparison.Ordinal)
                              && IsLiteralDefineMethodArgsText(IsNullNode(argsNode) ? null : argsNode.Text);
            hints.Add(new RubyMetaprogrammingHint(
                pattern,
                new RubyByteRange(metaCallNode.StartIndex, metaCallNode.EndIndex),
                extractable));
        }

        foreach (var match in methodMissingDefinitions)
        {
            var methodNode = match.FirstOrDefault(c => c.Name == "method_missing_def").Node;
            if (IsNullNode(methodNode))
            {
                continue;
            }

            hints.Add(new RubyMetaprogrammingHint(
                "method_missing",
                new RubyByteRange(methodNode.StartIndex, methodNode.EndIndex),
                Extractable: false));
        }

        return hints;
    }

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

            var group = RubyQueries.ClassifyPattern(match.PatternIndex);
            var bucket = group switch
            {
                RubyPatternGroup.ClassDeclarations => result.ClassDeclarations,
                RubyPatternGroup.ModuleDeclarations => result.ModuleDeclarations,
                RubyPatternGroup.VisibilityBare => result.VisibilityBare,
                RubyPatternGroup.VisibilityTargeted => result.VisibilityTargeted,
                RubyPatternGroup.MethodDeclarations => result.MethodDeclarations,
                RubyPatternGroup.YieldSites => result.YieldSites,
                RubyPatternGroup.BlockParameters => result.BlockParameters,
                RubyPatternGroup.SingletonMethods => result.SingletonMethods,
                RubyPatternGroup.Mixins => result.Mixins,
                RubyPatternGroup.Constants => result.Constants,
                RubyPatternGroup.AttributeAccessors => result.AttributeAccessors,
                RubyPatternGroup.RequireStatements => result.RequireStatements,
                RubyPatternGroup.AliasStatements => result.AliasStatements,
                RubyPatternGroup.AliasMethodCalls => result.AliasMethodCalls,
                RubyPatternGroup.MetaprogrammingCalls => result.MetaprogrammingCalls,
                RubyPatternGroup.MethodMissingDefinitions => result.MethodMissingDefinitions,
                _ => throw new InvalidOperationException($"Unknown pattern group {group}")
            };
            bucket.Add(captures);
        }

        return result;
    }

    private sealed class DispatchedMatches
    {
        public List<List<CaptureWithNode>> ClassDeclarations { get; } = [];
        public List<List<CaptureWithNode>> ModuleDeclarations { get; } = [];
        public List<List<CaptureWithNode>> VisibilityBare { get; } = [];
        public List<List<CaptureWithNode>> VisibilityTargeted { get; } = [];
        public List<List<CaptureWithNode>> MethodDeclarations { get; } = [];
        public List<List<CaptureWithNode>> YieldSites { get; } = [];
        public List<List<CaptureWithNode>> BlockParameters { get; } = [];
        public List<List<CaptureWithNode>> SingletonMethods { get; } = [];
        public List<List<CaptureWithNode>> Mixins { get; } = [];
        public List<List<CaptureWithNode>> Constants { get; } = [];
        public List<List<CaptureWithNode>> AttributeAccessors { get; } = [];
        public List<List<CaptureWithNode>> RequireStatements { get; } = [];
        public List<List<CaptureWithNode>> AliasStatements { get; } = [];
        public List<List<CaptureWithNode>> AliasMethodCalls { get; } = [];
        public List<List<CaptureWithNode>> MetaprogrammingCalls { get; } = [];
        public List<List<CaptureWithNode>> MethodMissingDefinitions { get; } = [];
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

    private static bool Contains(Node outer, Node inner)
        => outer.StartIndex <= inner.StartIndex && outer.EndIndex >= inner.EndIndex;

    private static bool IsInsideSingletonClass(Node methodNode, Node ownerNode)
    {
        Node? current = methodNode.Parent;
        while (!IsNullNode(current) && current != ownerNode)
        {
            if (current!.Type == "singleton_class")
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static Node? FindOwningTypeNode(Node node)
    {
        Node? current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type is "class" or "module")
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Node? GetOwnerNode(Node node, string ownerType)
    {
        Node? current = node;
        while (!IsNullNode(current))
        {
            if (current!.Type == ownerType)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static int CountNestingDepth(Node node)
    {
        var depth = 0;
        Node? current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type is "class" or "module")
            {
                depth++;
            }

            current = current.Parent;
        }

        return depth;
    }

    private static string BuildQualifiedName(Node node, string localName)
    {
        var segments = new Stack<string>();
        segments.Push(localName);

        Node? current = node.Parent;
        while (!IsNullNode(current))
        {
            if (current!.Type is "class" or "module")
            {
                var nameNode = current["name"];
                if (!IsNullNode(nameNode))
                {
                    segments.Push(NormalizeName(nameNode!.Text));
                }
            }

            current = current.Parent;
        }

        return string.Join("::", segments);
    }

    private static string? TryGetSuperclass(Node classNode)
    {
        Node? superclassNode;
        try
        {
            superclassNode = classNode["superclass"];
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        if (IsNullNode(superclassNode))
        {
            return null;
        }

        var raw = NormalizeName(superclassNode!.Text).TrimStart('<').Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static bool IsVisibility(string text)
        => NormalizeName(text) is "public" or "private" or "protected";

    private static bool IsNullNode(Node? node)
        => node is null || node.Id == IntPtr.Zero;

    private static string NormalizeName(string text)
        => text.Trim().Trim('"', '\'');

    private static string NormalizeSymbolName(string text)
    {
        var value = NormalizeName(text);
        if (value.StartsWith(':'))
        {
            value = value[1..];
        }

        return value;
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static bool IsLiteralDefineMethodArgsText(string? argsText)
    {
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return false;
        }

        var trimmed = argsText.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        var firstArg = comma >= 0 ? trimmed[..comma].Trim() : trimmed;

        if (firstArg.StartsWith(':'))
        {
            var symbolName = firstArg[1..];
            return !string.IsNullOrWhiteSpace(symbolName)
                   && symbolName.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '?' or '!' or '=');
        }

        if ((firstArg.StartsWith('"') && firstArg.EndsWith('"'))
            || (firstArg.StartsWith('\'') && firstArg.EndsWith('\'')))
        {
            return firstArg.Length >= 2;
        }

        return false;
    }

    private static string GetNodeKey(Node node)
        => $"{node.StartIndex}:{node.EndIndex}:{node.Type}";

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-ruby", "tree_sitter_ruby");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter Ruby grammar from TreeSitter.DotNet. Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RubyTreeSitterClient));
        }
    }

    private readonly record struct CaptureWithNode(string Name, Node Node);

    private abstract class OwnerBuilder
    {
        protected OwnerBuilder(Node node, string name, string qualifiedName, RubyByteRange byteRange)
        {
            Node = node;
            Name = name;
            QualifiedName = qualifiedName;
            ByteRange = byteRange;
        }

        public Node Node { get; }
        public string Name { get; }
        public string QualifiedName { get; }
        public RubyByteRange ByteRange { get; }
        public List<RubyMethodInfo> Methods { get; } = [];
        public List<RubyConstantInfo> Constants { get; } = [];
        public List<RubyAttributeInfo> Attributes { get; } = [];
        public List<RubyMixinInfo> Mixins { get; } = [];
        public List<(int Position, string Visibility)> VisibilityTransitions { get; } = [];
        public Dictionary<string, string> TargetedVisibilities { get; } = new(StringComparer.Ordinal);

        public string ResolveVisibility(string methodName, int methodStart)
        {
            if (TargetedVisibilities.TryGetValue(methodName, out var specific))
            {
                return specific;
            }

            return ResolveVisibilityAt(methodStart);
        }

        public string ResolveVisibilityAt(int position)
        {
            var current = "public";
            foreach (var transition in VisibilityTransitions.OrderBy(t => t.Position))
            {
                if (transition.Position > position)
                {
                    break;
                }

                current = transition.Visibility;
            }

            return current;
        }
    }

    private sealed class ClassBuilder : OwnerBuilder
    {
        public ClassBuilder(
            Node node,
            string name,
            string qualifiedName,
            string? superclass,
            bool hasSuperclassDeclaration,
            RubyByteRange byteRange)
            : base(node, name, qualifiedName, byteRange)
        {
            Superclass = superclass;
            HasSuperclassDeclaration = hasSuperclassDeclaration;
        }

        public string? Superclass { get; }
        public bool HasSuperclassDeclaration { get; }
        public List<RubySingletonMethodInfo> SingletonMethods { get; } = [];

        public RubyClassInfo ToSurface()
            => new(
                Name,
                QualifiedName,
                Superclass,
                HasSuperclassDeclaration,
                Methods.OrderBy(m => m.ByteRange.StartByte).ToList(),
                SingletonMethods.OrderBy(m => m.ByteRange.StartByte).ToList(),
                Constants.OrderBy(c => c.ByteRange.StartByte).ToList(),
                Attributes.OrderBy(a => a.ByteRange.StartByte).ToList(),
                Mixins.OrderBy(m => m.Ordinal).ToList(),
                ByteRange);
    }

    private sealed class ModuleBuilder : OwnerBuilder
    {
        public ModuleBuilder(Node node, string name, string qualifiedName, int nestingDepth, RubyByteRange byteRange)
            : base(node, name, qualifiedName, byteRange)
        {
            NestingDepth = nestingDepth;
        }

        public int NestingDepth { get; }

        public RubyModuleInfo ToSurface()
            => new(
                Name,
                QualifiedName,
                NestingDepth,
                Methods.OrderBy(m => m.ByteRange.StartByte).ToList(),
                Constants.OrderBy(c => c.ByteRange.StartByte).ToList(),
                Mixins.OrderBy(m => m.Ordinal).ToList(),
                ByteRange);
    }
}
