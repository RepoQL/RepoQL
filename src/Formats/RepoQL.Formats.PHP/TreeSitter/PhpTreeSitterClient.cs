using RepoQL.Formats.PHP.Surface;
using TreeSitter;

namespace RepoQL.Formats.PHP.TreeSitter;

/// <summary>
/// Parses PHP source code using tree-sitter and extracts structural information.
///
/// Purpose: Replace the ANTLR-based parser with tree-sitter for better error tolerance,
/// precise byte offsets, no memory leaks, and consistency with other format loaders.
///
/// Complexity: Thread-safe parser pooling, query-based extraction for all PHP constructs
/// (classes, interfaces, traits, enums, functions, methods, properties, constants, namespaces, use statements).
/// </summary>
public sealed class PhpTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();
    private static readonly Query SharedCombinedQuery = SharedLanguage.CreateQuery(PhpQueries.CombinedQuery);
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    private sealed record CaptureWithNode(string Name, Node Node);

    public PhpDocumentSurface Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (string.IsNullOrEmpty(sourceCode))
        {
            return new PhpDocumentSurface(
                Namespace: null,
                NamespaceSpan: null,
                UseStatements: [],
                Classes: [],
                Interfaces: [],
                Traits: [],
                Enums: [],
                Functions: [],
                Stats: new PhpParseStats(0, 0, 0, 0, 0, 0, 0),
                ErrorNodeCount: 0);
        }

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            using var tree = parser.Parse(sourceCode);
            var root = tree.RootNode;

            var errorNodeCount = CountErrorNodes(root);
            var dispatched = ExecuteCombinedQuery(root);

            var (ns, nsSpan) = ExtractNamespace(dispatched.Namespace);
            var useStatements = ExtractUseStatements(dispatched.UseDeclarations);
            var classes = ExtractClasses(dispatched.Classes, ns);
            var interfaces = ExtractInterfaces(dispatched.Interfaces, ns);
            var traits = ExtractTraits(dispatched.Traits, ns);
            var enums = ExtractEnums(dispatched.Enums, ns);
            var functions = ExtractFunctions(dispatched.Functions, ns);

            var methodCount = classes.Sum(c => c.Methods.Count)
                              + interfaces.Sum(i => i.Methods.Count)
                              + traits.Sum(t => t.Methods.Count)
                              + enums.Sum(e => e.Methods.Count);

            var lineCount = CountLines(sourceCode);
            var stats = new PhpParseStats(
                LineCount: lineCount,
                ClassCount: classes.Count,
                InterfaceCount: interfaces.Count,
                TraitCount: traits.Count,
                EnumCount: enums.Count,
                FunctionCount: functions.Count,
                MethodCount: methodCount);

            return new PhpDocumentSurface(
                Namespace: ns,
                NamespaceSpan: nsSpan,
                UseStatements: useStatements,
                Classes: classes,
                Interfaces: interfaces,
                Traits: traits,
                Enums: enums,
                Functions: functions,
                Stats: stats,
                ErrorNodeCount: errorNodeCount);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Failed to load TreeSitter.DotNet native PHP parser. Verify TreeSitter.DotNet is restored for this platform.",
                ex);
        }
    }

    #region Namespace

    private static (string? Namespace, PhpByteRange? Span) ExtractNamespace(List<List<CaptureWithNode>> matches)
    {
        if (matches.Count == 0)
            return (null, null);

        var match = matches[0];
        var nsNameNode = FindCapture(match, "namespace_name");
        var nsNode = FindCapture(match, "namespace_node");

        if (nsNameNode is null)
            return (null, null);

        return (nsNameNode.Text, nsNode is not null ? ToByteRange(nsNode) : ToByteRange(nsNameNode));
    }

    #endregion

    #region Use Statements

    private static List<PhpUseInfo> ExtractUseStatements(List<List<CaptureWithNode>> matches)
    {
        var results = new List<PhpUseInfo>();

        foreach (var match in matches)
        {
            var clauseNode = FindCapture(match, "use_clause");
            if (clauseNode is null)
                continue;

            var qualifiedName = FindNamedChild(clauseNode, "qualified_name");
            var aliasNode = FindDirectNameChild(clauseNode);
            var name = qualifiedName?.Text ?? clauseNode.Text;

            // If there's an alias (the `as` clause), it's the last `name` child that isn't part of qualified_name
            string? alias = null;
            if (aliasNode is not null && qualifiedName is not null && aliasNode.StartIndex > qualifiedName.EndIndex)
            {
                alias = aliasNode.Text;
            }

            results.Add(new PhpUseInfo(
                Name: name,
                Alias: alias,
                Span: ToByteRange(clauseNode)));
        }

        return results;
    }

    private static Node? FindDirectNameChild(Node node)
    {
        // Find the last `name` child directly under the clause (the alias, if present)
        Node? last = null;
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "name")
                last = child;
        }
        return last;
    }

    #endregion

    #region Classes

    private static List<PhpClassInfo> ExtractClasses(List<List<CaptureWithNode>> matches, string? currentNamespace)
    {
        var results = new List<PhpClassInfo>();

        foreach (var match in matches)
        {
            var classNode = FindCapture(match, "class_node");
            var nameNode = FindCapture(match, "class_name");
            if (classNode is null || nameNode is null)
                continue;

            var name = nameNode.Text;
            var isAbstract = HasChildOfType(classNode, "abstract_modifier");
            var isFinal = HasChildOfType(classNode, "final_modifier");
            var isReadonly = HasChildOfType(classNode, "readonly_modifier");

            var baseClause = FindNamedChild(classNode, "base_clause");
            string? extends_ = null;
            if (baseClause is not null)
            {
                var baseName = FindNamedChild(baseClause, "name") ?? FindNamedChild(baseClause, "qualified_name");
                extends_ = baseName?.Text;
            }

            var implements_ = ExtractInterfaceNames(classNode, "class_interface_clause");
            var declList = FindNamedChild(classNode, "declaration_list");
            var usesTraits = declList is not null ? ExtractTraitUses(declList) : [];
            var methods = declList is not null ? ExtractMethods(declList) : [];
            var properties = declList is not null ? ExtractProperties(declList) : [];
            var constants = declList is not null ? ExtractConstants(declList) : [];

            results.Add(new PhpClassInfo(
                Name: name,
                Namespace: currentNamespace,
                IsAbstract: isAbstract,
                IsFinal: isFinal,
                IsReadonly: isReadonly,
                Extends: extends_,
                Implements: implements_,
                UsesTraits: usesTraits,
                Methods: methods,
                Properties: properties,
                Constants: constants,
                Span: ToByteRange(classNode),
                NameSpan: ToByteRange(nameNode)));
        }

        return results;
    }

    #endregion

    #region Interfaces

    private static List<PhpInterfaceInfo> ExtractInterfaces(List<List<CaptureWithNode>> matches, string? currentNamespace)
    {
        var results = new List<PhpInterfaceInfo>();

        foreach (var match in matches)
        {
            var ifaceNode = FindCapture(match, "interface_node");
            var nameNode = FindCapture(match, "interface_name");
            if (ifaceNode is null || nameNode is null)
                continue;

            var name = nameNode.Text;
            var extends_ = ExtractInterfaceNames(ifaceNode, "base_clause");
            var declList = FindNamedChild(ifaceNode, "declaration_list");
            var methods = declList is not null ? ExtractMethods(declList) : [];
            var constants = declList is not null ? ExtractConstants(declList) : [];

            results.Add(new PhpInterfaceInfo(
                Name: name,
                Namespace: currentNamespace,
                Extends: extends_,
                Methods: methods,
                Constants: constants,
                Span: ToByteRange(ifaceNode),
                NameSpan: ToByteRange(nameNode)));
        }

        return results;
    }

    #endregion

    #region Traits

    private static List<PhpTraitInfo> ExtractTraits(List<List<CaptureWithNode>> matches, string? currentNamespace)
    {
        var results = new List<PhpTraitInfo>();

        foreach (var match in matches)
        {
            var traitNode = FindCapture(match, "trait_node");
            var nameNode = FindCapture(match, "trait_name");
            if (traitNode is null || nameNode is null)
                continue;

            var name = nameNode.Text;
            var declList = FindNamedChild(traitNode, "declaration_list");
            var methods = declList is not null ? ExtractMethods(declList) : [];
            var properties = declList is not null ? ExtractProperties(declList) : [];

            results.Add(new PhpTraitInfo(
                Name: name,
                Namespace: currentNamespace,
                Methods: methods,
                Properties: properties,
                Span: ToByteRange(traitNode),
                NameSpan: ToByteRange(nameNode)));
        }

        return results;
    }

    #endregion

    #region Enums

    private static List<PhpEnumInfo> ExtractEnums(List<List<CaptureWithNode>> matches, string? currentNamespace)
    {
        var results = new List<PhpEnumInfo>();

        foreach (var match in matches)
        {
            var enumNode = FindCapture(match, "enum_node");
            var nameNode = FindCapture(match, "enum_name");
            if (enumNode is null || nameNode is null)
                continue;

            var name = nameNode.Text;

            // Backed type is a primitive_type child directly on the enum_declaration
            string? backedType = null;
            var primitiveType = FindNamedChild(enumNode, "primitive_type");
            if (primitiveType is not null)
                backedType = primitiveType.Text;

            var implements_ = ExtractInterfaceNames(enumNode, "class_interface_clause");
            var declList = FindNamedChild(enumNode, "enum_declaration_list");
            var cases = declList is not null ? ExtractEnumCases(declList) : [];
            var methods = declList is not null ? ExtractMethodsFromEnumBody(declList) : [];

            results.Add(new PhpEnumInfo(
                Name: name,
                Namespace: currentNamespace,
                BackedType: backedType,
                Implements: implements_,
                Cases: cases,
                Methods: methods,
                Span: ToByteRange(enumNode),
                NameSpan: ToByteRange(nameNode)));
        }

        return results;
    }

    private static List<PhpEnumCaseInfo> ExtractEnumCases(Node declList)
    {
        var results = new List<PhpEnumCaseInfo>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "enum_case")
                continue;

            var nameNode = FindNamedChild(child, "name");
            if (nameNode is null)
                continue;

            results.Add(new PhpEnumCaseInfo(
                Name: nameNode.Text,
                Span: ToByteRange(child),
                NameSpan: ToByteRange(nameNode)));
        }
        return results;
    }

    private static List<PhpMethodInfo> ExtractMethodsFromEnumBody(Node declList)
    {
        var results = new List<PhpMethodInfo>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "method_declaration")
                continue;

            var method = ExtractMethodInfo(child);
            if (method is not null)
                results.Add(method);
        }
        return results;
    }

    #endregion

    #region Functions

    private static List<PhpFunctionInfo> ExtractFunctions(List<List<CaptureWithNode>> matches, string? currentNamespace)
    {
        var results = new List<PhpFunctionInfo>();

        foreach (var match in matches)
        {
            var funcNode = FindCapture(match, "function_node");
            var nameNode = FindCapture(match, "function_name");
            if (funcNode is null || nameNode is null)
                continue;

            var name = nameNode.Text;
            var returnType = ExtractReturnType(funcNode);
            var parameters = ExtractParameters(funcNode);

            results.Add(new PhpFunctionInfo(
                Name: name,
                Namespace: currentNamespace,
                ReturnType: returnType,
                Parameters: parameters,
                Span: ToByteRange(funcNode),
                NameSpan: ToByteRange(nameNode)));
        }

        return results;
    }

    #endregion

    #region Member Extraction Helpers

    private static List<PhpMethodInfo> ExtractMethods(Node declList)
    {
        var results = new List<PhpMethodInfo>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "method_declaration")
                continue;

            var method = ExtractMethodInfo(child);
            if (method is not null)
                results.Add(method);
        }
        return results;
    }

    private static PhpMethodInfo? ExtractMethodInfo(Node methodNode)
    {
        var nameNode = FindNamedChild(methodNode, "name");
        if (nameNode is null)
            return null;

        var (accessibility, isStatic, isAbstract, isFinal) = ExtractModifiers(methodNode);
        var returnType = ExtractReturnType(methodNode);
        var parameters = ExtractParameters(methodNode);

        return new PhpMethodInfo(
            Name: nameNode.Text,
            Accessibility: accessibility,
            IsStatic: isStatic,
            IsAbstract: isAbstract,
            IsFinal: isFinal,
            ReturnType: returnType,
            Parameters: parameters,
            Span: ToByteRange(methodNode),
            NameSpan: ToByteRange(nameNode));
    }

    private static List<PhpPropertyInfo> ExtractProperties(Node declList)
    {
        var results = new List<PhpPropertyInfo>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "property_declaration")
                continue;

            var (accessibility, isStatic, _, _) = ExtractModifiers(child);
            var isReadonly = HasChildOfType(child, "readonly_modifier");
            var type = ExtractPropertyType(child);
            var hasDefault = false;

            foreach (var propElement in child.NamedChildren)
            {
                if (propElement.Type != "property_element")
                    continue;

                var varName = FindNamedChild(propElement, "variable_name");
                if (varName is null)
                    continue;

                // Check for default value (property_initializer child of property_element)
                hasDefault = FindNamedChild(propElement, "property_initializer") is not null;

                results.Add(new PhpPropertyInfo(
                    Name: varName.Text,
                    Accessibility: accessibility,
                    IsStatic: isStatic,
                    IsReadonly: isReadonly,
                    Type: type,
                    HasDefault: hasDefault,
                    Span: ToByteRange(child),
                    NameSpan: ToByteRange(varName)));
            }
        }
        return results;
    }

    private static List<PhpConstantInfo> ExtractConstants(Node declList)
    {
        var results = new List<PhpConstantInfo>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "const_declaration")
                continue;

            string? accessibility = null;
            var visMod = FindNamedChild(child, "visibility_modifier");
            if (visMod is not null)
                accessibility = visMod.Text;

            foreach (var constElement in child.NamedChildren)
            {
                if (constElement.Type != "const_element")
                    continue;

                var nameNode = FindNamedChild(constElement, "name");
                if (nameNode is null)
                    continue;

                results.Add(new PhpConstantInfo(
                    Name: nameNode.Text,
                    Accessibility: accessibility,
                    Span: ToByteRange(child),
                    NameSpan: ToByteRange(nameNode)));
            }
        }
        return results;
    }

    private static List<string> ExtractTraitUses(Node declList)
    {
        var results = new List<string>();
        foreach (var child in declList.NamedChildren)
        {
            if (child.Type != "use_declaration")
                continue;

            foreach (var nameChild in child.NamedChildren)
            {
                if (nameChild.Type is "name" or "qualified_name")
                    results.Add(nameChild.Text);
            }
        }
        return results;
    }

    private static List<string> ExtractInterfaceNames(Node typeNode, string clauseType)
    {
        var results = new List<string>();
        var clause = FindNamedChild(typeNode, clauseType);
        if (clause is null)
            return results;

        foreach (var child in clause.NamedChildren)
        {
            if (child.Type is "name" or "qualified_name")
                results.Add(child.Text);
        }
        return results;
    }

    private static (string? Accessibility, bool IsStatic, bool IsAbstract, bool IsFinal) ExtractModifiers(Node node)
    {
        string? accessibility = null;
        var isStatic = false;
        var isAbstract = false;
        var isFinal = false;

        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "visibility_modifier":
                    accessibility = child.Text;
                    break;
                case "static_modifier":
                    isStatic = true;
                    break;
                case "abstract_modifier":
                    isAbstract = true;
                    break;
                case "final_modifier":
                    isFinal = true;
                    break;
            }
        }

        return (accessibility, isStatic, isAbstract, isFinal);
    }

    private static string? ExtractReturnType(Node funcOrMethodNode)
    {
        // Return type comes after formal_parameters. In tree-sitter-php it can be:
        // primitive_type, named_type, optional_type, union_type, intersection_type, nullable_type
        var foundParams = false;
        foreach (var child in funcOrMethodNode.NamedChildren)
        {
            if (child.Type == "formal_parameters")
            {
                foundParams = true;
                continue;
            }

            if (foundParams && child.Type is not "compound_statement" and not "declaration_list"
                and not "enum_declaration_list")
            {
                return child.Text;
            }

            // Stop if we hit the body
            if (child.Type is "compound_statement" or "declaration_list" or "enum_declaration_list")
                break;
        }

        return null;
    }

    private static string? ExtractPropertyType(Node propertyDecl)
    {
        // Type hint is a child before property_element: primitive_type, named_type, optional_type, etc.
        foreach (var child in propertyDecl.NamedChildren)
        {
            if (child.Type == "property_element")
                break;

            if (child.Type is "primitive_type" or "named_type" or "optional_type"
                or "union_type" or "intersection_type" or "nullable_type")
            {
                return child.Text;
            }
        }
        return null;
    }

    private static List<string> ExtractParameters(Node funcOrMethodNode)
    {
        var results = new List<string>();
        var paramsNode = FindNamedChild(funcOrMethodNode, "formal_parameters");
        if (paramsNode is null)
            return results;

        foreach (var param in paramsNode.NamedChildren)
        {
            if (param.Type is not "simple_parameter" and not "variadic_parameter"
                and not "property_promotion_parameter")
                continue;

            // Build parameter string like "int $id" or "?User $user"
            string? type = null;
            string? varName = null;
            var isVariadic = param.Type == "variadic_parameter";

            foreach (var child in param.NamedChildren)
            {
                if (child.Type is "primitive_type" or "named_type" or "optional_type"
                    or "union_type" or "intersection_type" or "nullable_type")
                {
                    type = child.Text;
                }
                else if (child.Type == "variable_name")
                {
                    varName = child.Text;
                }
            }

            if (varName is not null)
            {
                var prefix = isVariadic ? "..." : "";
                results.Add(type is not null ? $"{type} {prefix}{varName}" : $"{prefix}{varName}");
            }
        }

        return results;
    }

    #endregion

    #region Tree Utilities

    private static Node? FindNamedChild(Node node, string type)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == type)
                return child;
        }
        return null;
    }

    private static bool HasChildOfType(Node node, string type)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == type)
                return true;
        }
        return false;
    }

    private static PhpByteRange ToByteRange(Node node)
        => new(node.StartIndex, node.EndIndex);

    private static int CountErrorNodes(Node node)
    {
        var count = (node.IsError || node.IsMissing) ? 1 : 0;

        foreach (var child in node.NamedChildren)
            count += CountErrorNodes(child);

        return count;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                count++;
        }
        return count;
    }

    #endregion

    #region Query Execution

    private static Node? FindCapture(List<CaptureWithNode> captures, string name)
    {
        foreach (var capture in captures)
        {
            if (capture.Name == name)
                return capture.Node;
        }
        return null;
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
                continue;

            var group = PhpQueries.ClassifyPattern(match.PatternIndex);
            var bucket = group switch
            {
                PhpPatternGroup.Namespace => result.Namespace,
                PhpPatternGroup.UseDeclarations => result.UseDeclarations,
                PhpPatternGroup.Classes => result.Classes,
                PhpPatternGroup.Interfaces => result.Interfaces,
                PhpPatternGroup.Traits => result.Traits,
                PhpPatternGroup.Enums => result.Enums,
                PhpPatternGroup.Functions => result.Functions,
                _ => throw new InvalidOperationException($"Unknown pattern group {group}")
            };
            bucket.Add(captures);
        }

        return result;
    }

    private sealed class DispatchedMatches
    {
        public List<List<CaptureWithNode>> Namespace { get; } = [];
        public List<List<CaptureWithNode>> UseDeclarations { get; } = [];
        public List<List<CaptureWithNode>> Classes { get; } = [];
        public List<List<CaptureWithNode>> Interfaces { get; } = [];
        public List<List<CaptureWithNode>> Traits { get; } = [];
        public List<List<CaptureWithNode>> Enums { get; } = [];
        public List<List<CaptureWithNode>> Functions { get; } = [];
    }

    #endregion

    #region Lifecycle

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-php", "tree_sitter_php");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter PHP grammar from TreeSitter.DotNet. "
                + "Ensure the TreeSitter.DotNet NuGet package is properly restored.",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var parser in _parsers.Values)
            parser.Dispose();

        _parsers.Dispose();
    }

    #endregion
}
