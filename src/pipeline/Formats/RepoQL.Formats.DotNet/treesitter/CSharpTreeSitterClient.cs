using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using TreeSitter;

namespace RepoQL.Formats.DotNet.TreeSitter;

/// <summary>
/// Parses C# source code using tree-sitter and extracts structural information
/// into a <see cref="CSharpDocumentSurface"/>.
/// Purpose: Replace Roslyn syntax-only parsing with a NativeAOT-compatible parser.
/// Complexity: Thread-safe parser pooling, single-pass combined query extraction,
/// tree-parent-based nesting resolution for namespaces and types.
/// </summary>
internal sealed class CSharpTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private static readonly Query SharedCombinedQuery = SharedLanguage.CreateQuery(CSharpQueries.CombinedQuery);

    private static readonly Regex SummaryTagRegex = new(
        @"<summary>\s*(.*?)\s*</summary>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public CSharpDocumentSurface Parse(Guid documentId, string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new CSharpDocumentSurface
            {
                DocumentId = documentId,
                DocumentProperties = new JsonObject(),
                Namespaces = [],
                Types = [],
                Members = [],
                Usings = []
            };
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;
            var lineMap = new TextLineMap(sourceCode);

            var dispatched = ExecuteCombinedQuery(root);

            // Build namespaces first — types need to look up their containing namespace.
            var (namespaces, namespaceByNodeKey) = ExtractNamespaces(dispatched.NamespaceDeclarations, documentId, lineMap);

            // Build types — members need to look up their containing type.
            var (types, typeByNodeKey) = ExtractTypes(dispatched, documentId, lineMap, namespaceByNodeKey);

            // Build members — each needs its containing type.
            var members = ExtractMembers(dispatched, documentId, lineMap, typeByNodeKey);

            // Usings are flat — no nesting needed.
            var usings = ExtractUsings(dispatched.UsingDirectives, documentId, lineMap);

            var errorNodeCount = CountErrorNodes(root);

            return new CSharpDocumentSurface
            {
                DocumentId = documentId,
                DocumentProperties = BuildDocumentProperties(lineMap, namespaces, types, members, usings, errorNodeCount),
                Namespaces = namespaces,
                Types = types,
                Members = members,
                Usings = usings
            };
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native C# parser (tree-sitter-c-sharp). Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var parser in _parsers.Values) parser.Dispose();
        _parsers.Dispose();
        _disposed = true;
    }

    // ── Dispatch ─────────────────────────────────────────────

    private sealed record CaptureWithNode(string Name, Node Node);

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

            if (captures.Count == 0) continue;

            var group = CSharpQueries.ClassifyPattern(match.PatternIndex);
            var bucket = group switch
            {
                CSharpPatternGroup.UsingDirectives => result.UsingDirectives,
                CSharpPatternGroup.NamespaceDeclarations => result.NamespaceDeclarations,
                CSharpPatternGroup.ClassDeclarations => result.ClassDeclarations,
                CSharpPatternGroup.StructDeclarations => result.StructDeclarations,
                CSharpPatternGroup.RecordDeclarations => result.RecordDeclarations,
                CSharpPatternGroup.InterfaceDeclarations => result.InterfaceDeclarations,
                CSharpPatternGroup.EnumDeclarations => result.EnumDeclarations,
                CSharpPatternGroup.MethodDeclarations => result.MethodDeclarations,
                CSharpPatternGroup.ConstructorDeclarations => result.ConstructorDeclarations,
                CSharpPatternGroup.PropertyDeclarations => result.PropertyDeclarations,
                CSharpPatternGroup.FieldDeclarations => result.FieldDeclarations,
                CSharpPatternGroup.EventDeclarations => result.EventDeclarations,
                CSharpPatternGroup.IndexerDeclarations => result.IndexerDeclarations,
                CSharpPatternGroup.Comments => result.Comments,
                _ => throw new InvalidOperationException($"Unknown C# pattern group {group}")
            };
            bucket.Add(captures);
        }

        return result;
    }

    // ── Namespace extraction ─────────────────────────────────

    private static (IReadOnlyList<CSharpNamespaceInfo> List, Dictionary<string, CSharpNamespaceInfo> ByNodeKey) ExtractNamespaces(
        List<List<CaptureWithNode>> matches, Guid documentId, TextLineMap lineMap)
    {
        var list = new List<CSharpNamespaceInfo>();
        var byNodeKey = new Dictionary<string, CSharpNamespaceInfo>(StringComparer.Ordinal);

        foreach (var captures in matches)
        {
            var declNode = GetCaptureNode(captures, "namespace_decl");
            var nameNode = GetCaptureNode(captures, "namespace_name");
            if (IsNullNode(declNode) || IsNullNode(nameNode)) continue;

            var name = nameNode!.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Find parent namespace by walking the tree up.
            var parentNsNode = FindAncestorOfType(declNode!.Parent, "namespace_declaration", "file_scoped_namespace_declaration");
            CSharpNamespaceInfo? parentNs = null;
            if (!IsNullNode(parentNsNode))
            {
                byNodeKey.TryGetValue(GetNodeKey(parentNsNode!), out parentNs);
            }

            var qualifiedName = parentNs is not null
                ? $"{parentNs.QualifiedName}.{name}"
                : name;

            var span = ToDocumentSpan(declNode, lineMap);
            var nodeId = CSharpIdFactory.CreateNodeId(documentId, "namespace", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);
            var spanId = CSharpIdFactory.CreateSpanId(documentId, "namespace", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);

            var info = new CSharpNamespaceInfo(
                NodeId: nodeId,
                SpanId: spanId,
                ParentNamespaceId: parentNs?.NodeId,
                Name: name,
                QualifiedName: qualifiedName,
                Span: span);

            list.Add(info);
            byNodeKey[GetNodeKey(declNode)] = info;
        }

        return (list, byNodeKey);
    }

    // ── Type extraction ──────────────────────────────────────

    private static (IReadOnlyList<CSharpTypeInfo> List, Dictionary<string, CSharpTypeInfo> ByNodeKey) ExtractTypes(
        DispatchedMatches dispatched, Guid documentId, TextLineMap lineMap,
        Dictionary<string, CSharpNamespaceInfo> namespaceByNodeKey)
    {
        var list = new List<CSharpTypeInfo>();
        var byNodeKey = new Dictionary<string, CSharpTypeInfo>(StringComparer.Ordinal);

        // Process all type declaration groups in order, building the lookup incrementally
        // so that nested types can find their parent.
        ProcessTypeGroup(dispatched.ClassDeclarations, "class_decl", "class_name", "class", false);
        ProcessTypeGroup(dispatched.StructDeclarations, "struct_decl", "struct_name", "struct", false);
        ProcessTypeGroup(dispatched.RecordDeclarations, "record_decl", "record_name", "record", true);
        ProcessTypeGroup(dispatched.InterfaceDeclarations, "interface_decl", "interface_name", "interface", false);
        ProcessTypeGroup(dispatched.EnumDeclarations, "enum_decl", "enum_name", "enum", false);

        return (list, byNodeKey);

        void ProcessTypeGroup(List<List<CaptureWithNode>> matches, string declCapture, string nameCapture, string kind, bool isRecord)
        {
            foreach (var captures in matches)
            {
                var declNode = GetCaptureNode(captures, declCapture);
                var nameNode = GetCaptureNode(captures, nameCapture);
                if (IsNullNode(declNode) || IsNullNode(nameNode)) continue;

                var name = nameNode!.Text.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Find containing namespace.
                // For block-scoped namespaces: the type is nested inside the namespace node.
                // For file-scoped namespaces: the type is a sibling at compilation_unit level.
                var containingNs = FindContainingNamespace(declNode!, namespaceByNodeKey);

                // Find parent type (for nesting).
                var parentTypeNode = FindAncestorTypeDeclaration(declNode!.Parent);
                CSharpTypeInfo? parentType = null;
                if (!IsNullNode(parentTypeNode))
                    byNodeKey.TryGetValue(GetNodeKey(parentTypeNode!), out parentType);

                var containing = parentType?.QualifiedName ?? containingNs?.QualifiedName;
                var qualifiedName = string.IsNullOrWhiteSpace(containing) ? name : $"{containing}.{name}";

                var modifiers = ExtractModifiers(declNode);
                var accessibility = ResolveAccessibility(modifiers, parentType is null ? "internal" : "private");
                var (baseType, interfaces) = ExtractInheritance(declNode, kind);
                var summary = ExtractDocComment(declNode);

                var span = ToDocumentSpan(declNode, lineMap);
                var nodeId = CSharpIdFactory.CreateNodeId(documentId, "type", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);
                var spanId = CSharpIdFactory.CreateSpanId(documentId, "type", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);

                var typeModifiers = modifiers
                    .Where(m => m is "static" or "partial" or "sealed" or "abstract" or "readonly")
                    .ToList();

                var info = new CSharpTypeInfo(
                    NodeId: nodeId,
                    SpanId: spanId,
                    NamespaceNodeId: containingNs?.NodeId,
                    ParentTypeId: parentType?.NodeId,
                    Name: name,
                    QualifiedName: qualifiedName,
                    Kind: kind,
                    Accessibility: accessibility,
                    Namespace: containingNs?.QualifiedName,
                    IsPartial: modifiers.Contains("partial"),
                    IsStatic: modifiers.Contains("static"),
                    IsRecord: isRecord,
                    BaseType: baseType,
                    Interfaces: interfaces,
                    Span: span,
                    Modifiers: typeModifiers,
                    Summary: summary);

                list.Add(info);
                byNodeKey[GetNodeKey(declNode)] = info;
            }
        }
    }

    // ── Member extraction ────────────────────────────────────

    private static IReadOnlyList<CSharpMemberInfo> ExtractMembers(
        DispatchedMatches dispatched, Guid documentId, TextLineMap lineMap,
        Dictionary<string, CSharpTypeInfo> typeByNodeKey)
    {
        var members = new List<CSharpMemberInfo>();

        // Methods
        foreach (var captures in dispatched.MethodDeclarations)
        {
            var declNode = GetCaptureNode(captures, "method_decl");
            var nameNode = GetCaptureNode(captures, "method_name");
            var returnNode = GetCaptureNode(captures, "method_return");
            var paramsNode = GetCaptureNode(captures, "method_params");
            if (IsNullNode(declNode) || IsNullNode(nameNode)) continue;

            var containingType = FindContainingType(declNode!, typeByNodeKey);
            if (containingType is null) continue;

            var modifiers = ExtractModifiers(declNode!);
            members.Add(CreateMemberInfo(
                documentId, lineMap, declNode, nameNode!.Text.Trim(), "method",
                containingType, modifiers,
                returnType: IsNullNode(returnNode) ? null : returnNode!.Text.Trim(),
                parameterList: paramsNode,
                isAsync: modifiers.Contains("async")));
        }

        // Constructors
        foreach (var captures in dispatched.ConstructorDeclarations)
        {
            var declNode = GetCaptureNode(captures, "ctor_decl");
            var nameNode = GetCaptureNode(captures, "ctor_name");
            var paramsNode = GetCaptureNode(captures, "ctor_params");
            if (IsNullNode(declNode) || IsNullNode(nameNode)) continue;

            var containingType = FindContainingType(declNode!, typeByNodeKey);
            if (containingType is null) continue;

            var modifiers = ExtractModifiers(declNode!);
            members.Add(CreateMemberInfo(
                documentId, lineMap, declNode!, nameNode!.Text.Trim(), "constructor",
                containingType, modifiers,
                returnType: null,
                parameterList: paramsNode,
                isAsync: false));
        }

        // Properties
        foreach (var captures in dispatched.PropertyDeclarations)
        {
            var declNode = GetCaptureNode(captures, "prop_decl");
            var nameNode = GetCaptureNode(captures, "prop_name");
            var typeNode = GetCaptureNode(captures, "prop_type");
            if (IsNullNode(declNode) || IsNullNode(nameNode)) continue;

            var containingType = FindContainingType(declNode!, typeByNodeKey);
            if (containingType is null) continue;

            var modifiers = ExtractModifiers(declNode!);
            members.Add(CreateMemberInfo(
                documentId, lineMap, declNode!, nameNode!.Text.Trim(), "property",
                containingType, modifiers,
                returnType: IsNullNode(typeNode) ? null : typeNode!.Text.Trim(),
                parameterList: null,
                isAsync: false));
        }

        // Indexers
        foreach (var captures in dispatched.IndexerDeclarations)
        {
            var declNode = GetCaptureNode(captures, "indexer_decl");
            var typeNode = GetCaptureNode(captures, "indexer_type");
            if (IsNullNode(declNode)) continue;

            var containingType = FindContainingType(declNode!, typeByNodeKey);
            if (containingType is null) continue;

            var modifiers = ExtractModifiers(declNode!);
            // Indexer parameter list is in a bracketed_parameter_list child.
            var bracketedParams = TryGetChildByType(declNode!, "bracketed_parameter_list");
            members.Add(CreateMemberInfo(
                documentId, lineMap, declNode!, "this", "indexer",
                containingType, modifiers,
                returnType: IsNullNode(typeNode) ? null : typeNode!.Text.Trim(),
                parameterList: bracketedParams,
                isAsync: false));
        }

        // Fields (may have multiple variable declarators)
        foreach (var captures in dispatched.FieldDeclarations)
        {
            var declNode = GetCaptureNode(captures, "field_decl");
            if (IsNullNode(declNode)) continue;

            var containingType = FindContainingType(declNode!, typeByNodeKey);
            if (containingType is null) continue;

            var modifiers = ExtractModifiers(declNode!);
            var summary = ExtractDocComment(declNode!);
            var typeName = ExtractFieldTypeName(declNode!);

            foreach (var variable in EnumerateVariableDeclarators(declNode!))
            {
                var varName = ExtractVariableName(variable);
                if (string.IsNullOrWhiteSpace(varName)) continue;

                members.Add(CreateFieldMemberInfo(
                    documentId, lineMap, variable, varName, "field",
                    containingType, modifiers, typeName, summary));
            }
        }

        // Events (event_declaration — property-like events)
        foreach (var captures in dispatched.EventDeclarations)
        {
            var declNode = GetCaptureNode(captures, "event_decl");
            if (!IsNullNode(declNode))
            {
                var nameNode = GetCaptureNode(captures, "event_name");
                var typeNode = GetCaptureNode(captures, "event_type");
                if (!IsNullNode(nameNode))
                {
                    var containingType = FindContainingType(declNode!, typeByNodeKey);
                    if (containingType is not null)
                    {
                        var modifiers = ExtractModifiers(declNode!);
                        members.Add(CreateMemberInfo(
                            documentId, lineMap, declNode!, nameNode!.Text.Trim(), "event",
                            containingType, modifiers,
                            returnType: IsNullNode(typeNode) ? null : typeNode!.Text.Trim(),
                            parameterList: null,
                            isAsync: false));
                    }
                }
            }

            // event_field_declaration — field-like events (may have multiple variables)
            var fieldDeclNode = GetCaptureNode(captures, "event_field_decl");
            if (!IsNullNode(fieldDeclNode))
            {
                var containingType = FindContainingType(fieldDeclNode!, typeByNodeKey);
                if (containingType is not null)
                {
                    var modifiers = ExtractModifiers(fieldDeclNode!);
                    var summary = ExtractDocComment(fieldDeclNode!);
                    var typeName = ExtractEventFieldTypeName(fieldDeclNode!);

                    foreach (var variable in EnumerateVariableDeclarators(fieldDeclNode!))
                    {
                        var varName = ExtractVariableName(variable);
                        if (string.IsNullOrWhiteSpace(varName)) continue;

                        members.Add(CreateFieldMemberInfo(
                            documentId, lineMap, variable, varName, "event",
                            containingType, modifiers, typeName, summary));
                    }
                }
            }
        }

        return members;
    }

    // ── Using extraction ─────────────────────────────────────

    private static IReadOnlyList<CSharpUsingInfo> ExtractUsings(
        List<List<CaptureWithNode>> matches, Guid documentId, TextLineMap lineMap)
    {
        var list = new List<CSharpUsingInfo>();

        foreach (var captures in matches)
        {
            var declNode = GetCaptureNode(captures, "using_decl");
            if (IsNullNode(declNode)) continue;

            var (name, alias, isStatic) = ParseUsingDirective(declNode!);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var span = ToDocumentSpan(declNode!, lineMap);
            var nodeId = CSharpIdFactory.CreateNodeId(documentId, "using", declNode!.StartIndex, declNode.EndIndex - declNode.StartIndex);
            var spanId = CSharpIdFactory.CreateSpanId(documentId, "using", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);

            list.Add(new CSharpUsingInfo(
                NodeId: nodeId,
                SpanId: spanId,
                Name: name,
                Alias: alias,
                IsStatic: isStatic,
                Span: span));
        }

        return list;
    }

    // ── Member construction helpers ──────────────────────────

    private static CSharpMemberInfo CreateMemberInfo(
        Guid documentId, TextLineMap lineMap, Node declNode, string name, string kind,
        CSharpTypeInfo containingType, List<string> modifiers,
        string? returnType, Node? parameterList, bool isAsync)
    {
        var span = ToDocumentSpan(declNode, lineMap);
        var nodeId = CSharpIdFactory.CreateNodeId(documentId, "member", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);
        var spanId = CSharpIdFactory.CreateSpanId(documentId, "member", declNode.StartIndex, declNode.EndIndex - declNode.StartIndex);
        var summary = ExtractDocComment(declNode);

        var memberModifiers = modifiers
            .Where(m => m is "static" or "virtual" or "override" or "abstract" or "sealed"
                or "readonly" or "const" or "new" or "extern" or "volatile" or "async")
            .ToList();

        if (isAsync && !memberModifiers.Contains("async"))
            memberModifiers.Add("async");

        return new CSharpMemberInfo(
            NodeId: nodeId,
            SpanId: spanId,
            DeclaringTypeId: containingType.NodeId,
            Name: name,
            Kind: kind,
            Accessibility: ResolveAccessibility(modifiers, "private"),
            IsStatic: modifiers.Contains("static"),
            IsAsync: isAsync,
            ReturnType: returnType,
            DeclaringTypeDisplay: containingType.QualifiedName,
            Parameters: ExtractParameters(parameterList),
            Span: span,
            Modifiers: memberModifiers,
            Summary: summary);
    }

    private static CSharpMemberInfo CreateFieldMemberInfo(
        Guid documentId, TextLineMap lineMap, Node variableNode, string name, string kind,
        CSharpTypeInfo containingType, List<string> modifiers, string? typeName, string? summary)
    {
        var span = ToDocumentSpan(variableNode, lineMap);
        var nodeId = CSharpIdFactory.CreateNodeId(documentId, "member", variableNode.StartIndex, variableNode.EndIndex - variableNode.StartIndex);
        var spanId = CSharpIdFactory.CreateSpanId(documentId, "member", variableNode.StartIndex, variableNode.EndIndex - variableNode.StartIndex);

        var memberModifiers = modifiers
            .Where(m => m is "static" or "virtual" or "override" or "abstract" or "sealed"
                or "readonly" or "const" or "new" or "extern" or "volatile")
            .ToList();

        return new CSharpMemberInfo(
            NodeId: nodeId,
            SpanId: spanId,
            DeclaringTypeId: containingType.NodeId,
            Name: name,
            Kind: kind,
            Accessibility: ResolveAccessibility(modifiers, "private"),
            IsStatic: modifiers.Contains("static"),
            IsAsync: false,
            ReturnType: typeName,
            DeclaringTypeDisplay: containingType.QualifiedName,
            Parameters: [],
            Span: span,
            Modifiers: memberModifiers,
            Summary: summary);
    }

    // ── Modifier extraction ──────────────────────────────────

    private static List<string> ExtractModifiers(Node declNode)
    {
        var modifiers = new List<string>();
        foreach (var child in declNode.Children)
        {
            if (child.Type == "modifier")
            {
                var text = child.Text.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    modifiers.Add(text);
            }
        }
        return modifiers;
    }

    private static string ResolveAccessibility(List<string> modifiers, string fallback)
    {
        bool hasPublic = false, hasProtected = false, hasInternal = false, hasPrivate = false;
        foreach (var m in modifiers)
        {
            switch (m)
            {
                case "public": hasPublic = true; break;
                case "protected": hasProtected = true; break;
                case "internal": hasInternal = true; break;
                case "private": hasPrivate = true; break;
            }
        }

        if (hasPublic) return "public";
        if (hasProtected && hasInternal) return "protected internal";
        if (hasProtected && hasPrivate) return "private protected";
        if (hasInternal) return "internal";
        if (hasProtected) return "protected";
        if (hasPrivate) return "private";
        return fallback;
    }

    // ── Inheritance extraction ────────────────────────────────

    private static (string? BaseType, IReadOnlyList<string> Interfaces) ExtractInheritance(Node declNode, string kind)
    {
        var baseList = TryGetChildByType(declNode, "base_list");
        if (IsNullNode(baseList))
            return (null, []);

        // base_list children are name-like nodes (identifier, qualified_name, generic_name, etc.)
        // interspersed with `:` and `,` tokens.
        var baseTypes = baseList!.NamedChildren
            .Where(n => n.Type is not ":" and not ",")
            .Select(n => n.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (baseTypes.Count == 0)
            return (null, []);

        // Interfaces don't have base types — everything is an implemented interface.
        if (kind == "interface")
            return (null, baseTypes);

        // For classes/records/structs: first entry may be a base class, rest are interfaces.
        // Heuristic: if the first type starts with 'I' followed by an uppercase letter, treat it as an interface.
        var first = baseTypes[0];
        if (first.Length >= 2 && first[0] == 'I' && char.IsUpper(first[1]))
            return (null, baseTypes);

        return (first, baseTypes.Count > 1 ? baseTypes.GetRange(1, baseTypes.Count - 1) : []);
    }

    // ── Parameter extraction ─────────────────────────────────

    private static IReadOnlyList<CSharpParameterInfo> ExtractParameters(Node? parameterListNode)
    {
        if (IsNullNode(parameterListNode)) return [];

        var parameters = new List<CSharpParameterInfo>();
        foreach (var child in parameterListNode!.NamedChildren)
        {
            if (child.Type != "parameter") continue;

            var typeNode = TryGetField(child, "type");
            var nameNode = TryGetField(child, "name");
            var hasDefault = child.Children.Any(n => n.Type == "=");

            parameters.Add(new CSharpParameterInfo(
                Name: IsNullNode(nameNode) ? "" : nameNode!.Text.Trim(),
                Type: IsNullNode(typeNode) ? "object" : typeNode!.Text.Trim(),
                HasDefaultValue: hasDefault));
        }

        return parameters;
    }

    // ── Using directive parsing ──────────────────────────────

    private static (string Name, string? Alias, bool IsStatic) ParseUsingDirective(Node usingNode)
    {
        string? alias = null;
        var isStatic = false;
        var hasEquals = false;

        // Detect `static` keyword and `=` (alias indicator) in children.
        foreach (var child in usingNode.Children)
        {
            if (child.Type == "static") isStatic = true;
            if (child.Type == "=") hasEquals = true;
        }

        if (hasEquals)
        {
            // Alias using: `using Alias = Namespace.Name;`
            // Structure: identifier (alias) → = → qualified_name/identifier (name)
            var nameChildren = usingNode.NamedChildren
                .Where(c => c.Type is "qualified_name" or "identifier_name" or "identifier"
                    or "alias_qualified_name" or "generic_name")
                .ToList();

            if (nameChildren.Count >= 2)
            {
                alias = nameChildren[0].Text.Trim();
                return (nameChildren[1].Text.Trim(), alias, isStatic);
            }

            if (nameChildren.Count == 1)
            {
                // Single name after equals — the alias is before it
                return (nameChildren[0].Text.Trim(), null, isStatic);
            }
        }

        // Regular using: last name-like child is the namespace.
        var nameNode = usingNode.NamedChildren
            .LastOrDefault(c => c.Type is "qualified_name" or "identifier_name" or "identifier"
                or "alias_qualified_name" or "generic_name");
        var name = IsNullNode(nameNode) ? "" : nameNode!.Text.Trim();

        return (name, alias, isStatic);
    }

    // ── Doc comment extraction ───────────────────────────────

    private static string? ExtractDocComment(Node declNode)
    {
        // Find preceding comment siblings by walking the parent's children backward from this node.
        var parent = declNode.Parent;
        if (IsNullNode(parent)) return null;

        var siblings = parent!.Children.ToList();
        var myIndex = -1;
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].StartIndex == declNode.StartIndex && siblings[i].EndIndex == declNode.EndIndex)
            {
                myIndex = i;
                break;
            }
        }

        if (myIndex < 0) return null;

        var commentLines = new List<string>();
        for (var i = myIndex - 1; i >= 0; i--)
        {
            var sibling = siblings[i];
            if (sibling.Type != "comment") break;

            var text = sibling.Text;
            if (text.StartsWith("///", StringComparison.Ordinal))
            {
                commentLines.Insert(0, text);
            }
            else
            {
                break; // Stop at non-doc comments.
            }
        }

        if (commentLines.Count == 0) return null;

        var combined = string.Join("\n", commentLines);
        var match = SummaryTagRegex.Match(combined);
        if (!match.Success) return null;

        // Strip /// prefixes from the extracted summary content.
        var raw = match.Groups[1].Value;
        var cleaned = string.Join(" ",
            raw.Split('\n')
                .Select(line => line.TrimStart().TrimStart('/').TrimStart())
                .Where(line => !string.IsNullOrWhiteSpace(line)));

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned.Trim();
    }

    // ── Field helpers ────────────────────────────────────────

    private static string? ExtractFieldTypeName(Node fieldDeclNode)
    {
        // field_declaration → variable_declaration → type
        var varDecl = TryGetChildByType(fieldDeclNode, "variable_declaration");
        if (IsNullNode(varDecl)) return null;
        var typeNode = TryGetField(varDecl!, "type");
        return IsNullNode(typeNode) ? null : typeNode!.Text.Trim();
    }

    private static string? ExtractEventFieldTypeName(Node eventFieldDeclNode)
    {
        // event_field_declaration → variable_declaration → type
        var varDecl = TryGetChildByType(eventFieldDeclNode, "variable_declaration");
        if (IsNullNode(varDecl)) return null;
        var typeNode = TryGetField(varDecl!, "type");
        return IsNullNode(typeNode) ? null : typeNode!.Text.Trim();
    }

    private static IEnumerable<Node> EnumerateVariableDeclarators(Node declNode)
    {
        var varDecl = TryGetChildByType(declNode, "variable_declaration");
        if (IsNullNode(varDecl)) yield break;
        foreach (var child in varDecl!.NamedChildren)
        {
            if (child.Type == "variable_declarator")
                yield return child;
        }
    }

    private static string? ExtractVariableName(Node variableDeclaratorNode)
    {
        var nameNode = TryGetField(variableDeclaratorNode, "name");
        if (!IsNullNode(nameNode)) return nameNode!.Text.Trim();

        // Fallback: first identifier child.
        var identifier = variableDeclaratorNode.NamedChildren.FirstOrDefault(c => c.Type == "identifier");
        return IsNullNode(identifier) ? null : identifier!.Text.Trim();
    }

    // ── Type containment ─────────────────────────────────────

    private static CSharpNamespaceInfo? FindContainingNamespace(Node declNode, Dictionary<string, CSharpNamespaceInfo> namespaceByNodeKey)
    {
        // Walk up the tree looking for a block-scoped namespace ancestor.
        var nsNode = FindAncestorOfType(declNode.Parent, "namespace_declaration");
        if (!IsNullNode(nsNode))
        {
            namespaceByNodeKey.TryGetValue(GetNodeKey(nsNode!), out var ns);
            return ns;
        }

        // For file-scoped namespaces: the namespace is a sibling of the type at the compilation_unit level.
        // Find the nearest compilation_unit ancestor and look for a file_scoped_namespace_declaration child.
        var compilationUnit = FindAncestorOfType(declNode, "compilation_unit");
        if (!IsNullNode(compilationUnit))
        {
            var fileNs = TryGetChildByType(compilationUnit!, "file_scoped_namespace_declaration");
            if (!IsNullNode(fileNs))
            {
                namespaceByNodeKey.TryGetValue(GetNodeKey(fileNs!), out var ns);
                return ns;
            }
        }

        return null;
    }

    private static CSharpTypeInfo? FindContainingType(Node memberDeclNode, Dictionary<string, CSharpTypeInfo> typeByNodeKey)
    {
        var typeNode = FindAncestorTypeDeclaration(memberDeclNode.Parent);
        if (IsNullNode(typeNode)) return null;
        typeByNodeKey.TryGetValue(GetNodeKey(typeNode!), out var info);
        return info;
    }

    private static Node? FindAncestorTypeDeclaration(Node? node)
    {
        return FindAncestorOfType(node,
            "class_declaration", "struct_declaration", "record_declaration",
            "interface_declaration", "enum_declaration");
    }

    // ── Document properties ──────────────────────────────────

    private static JsonObject BuildDocumentProperties(
        TextLineMap lineMap, IReadOnlyList<CSharpNamespaceInfo> namespaces,
        IReadOnlyList<CSharpTypeInfo> types, IReadOnlyList<CSharpMemberInfo> members,
        IReadOnlyList<CSharpUsingInfo> usings, int errorNodeCount)
    {
        return new JsonObject
        {
            ["line_count"] = lineMap.LineCount,
            ["namespace_count"] = namespaces.Count,
            ["type_count"] = types.Count,
            ["member_count"] = members.Count,
            ["using_count"] = usings.Count,
            ["error_node_count"] = errorNodeCount
        };
    }

    // ── Tree-sitter utilities ────────────────────────────────

    private static Node? GetCaptureNode(IEnumerable<CaptureWithNode> captures, string name)
        => captures.FirstOrDefault(c => c.Name == name)?.Node;

    private static bool IsNullNode(Node? node)
        => node is null || node.Id == IntPtr.Zero;

    private static Node? TryGetField(Node node, string fieldName)
    {
        try { return node[fieldName]; }
        catch (KeyNotFoundException) { return null; }
    }

    private static Node? TryGetChildByType(Node parent, string type)
    {
        foreach (var child in parent.NamedChildren)
        {
            if (child.Type == type) return child;
        }
        return null;
    }

    private static Node? FindAncestorOfType(Node? node, params string[] types)
    {
        var current = node;
        while (!IsNullNode(current))
        {
            if (types.Contains(current!.Type))
                return current;
            current = current.Parent;
        }
        return null;
    }

    private static string GetNodeKey(Node node)
        => $"{node.StartIndex}:{node.EndIndex}:{node.Type}";

    private static DocumentSpan ToDocumentSpan(Node node, TextLineMap lineMap)
        => lineMap.GetSpan(node.StartIndex, node.EndIndex);

    private static int CountErrorNodes(Node root)
    {
        var count = 0;
        CountErrors(root, ref count);
        return count;

        static void CountErrors(Node node, ref int count)
        {
            if (node.IsError || node.Type == "ERROR") count++;
            foreach (var child in node.Children)
                CountErrors(child, ref count);
        }
    }

    private static int CountLines(string sourceCode)
    {
        if (string.IsNullOrEmpty(sourceCode)) return 0;
        var count = 1;
        foreach (var c in sourceCode)
        {
            if (c == '\n') count++;
        }
        return count;
    }

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter C# grammar from TreeSitter.DotNet (tree-sitter-c-sharp). Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CSharpTreeSitterClient));
    }

    // ── Dispatch container ───────────────────────────────────

    private sealed class DispatchedMatches
    {
        public List<List<CaptureWithNode>> UsingDirectives { get; } = [];
        public List<List<CaptureWithNode>> NamespaceDeclarations { get; } = [];
        public List<List<CaptureWithNode>> ClassDeclarations { get; } = [];
        public List<List<CaptureWithNode>> StructDeclarations { get; } = [];
        public List<List<CaptureWithNode>> RecordDeclarations { get; } = [];
        public List<List<CaptureWithNode>> InterfaceDeclarations { get; } = [];
        public List<List<CaptureWithNode>> EnumDeclarations { get; } = [];
        public List<List<CaptureWithNode>> MethodDeclarations { get; } = [];
        public List<List<CaptureWithNode>> ConstructorDeclarations { get; } = [];
        public List<List<CaptureWithNode>> PropertyDeclarations { get; } = [];
        public List<List<CaptureWithNode>> FieldDeclarations { get; } = [];
        public List<List<CaptureWithNode>> EventDeclarations { get; } = [];
        public List<List<CaptureWithNode>> IndexerDeclarations { get; } = [];
        public List<List<CaptureWithNode>> Comments { get; } = [];
    }
}
