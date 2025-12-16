using TreeSitter;

namespace RepoQL.Formats.PHP;

/// <summary>
/// Wrapper around TreeSitter.DotNet for parsing PHP source code.
/// Uses tree-sitter for structural parsing, then locates spans by text matching.
/// </summary>
public sealed class PHPTreeSitterClient : IDisposable
{
    private readonly Language _language;
    private readonly Parser _parser;
    private bool _disposed;

    public PHPTreeSitterClient()
    {
        _language = new Language("PHP");
        _parser = new Parser(_language);
    }

    /// <summary>
    /// Parse PHP source code and extract semantic information.
    /// </summary>
    public PHPParseResult Parse(string sourceCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sourceCode);

        using var tree = _parser.Parse(sourceCode);
        var result = new PHPParseResult();

        if (tree?.RootNode is null)
            return result;

        var locator = new SpanLocator(sourceCode);
        ExtractFromNode(tree.RootNode, sourceCode, result, null, locator);
        return result;
    }

    private void ExtractFromNode(Node node, string source, PHPParseResult result, string? currentNamespace, SpanLocator locator)
    {
        var nodeType = node.Type;

        switch (nodeType)
        {
            case "namespace_definition":
                currentNamespace = ExtractNamespace(node, source, result, locator);
                break;
            case "namespace_use_declaration":
                ExtractUseStatements(node, source, result, locator);
                break;
            case "class_declaration":
                ExtractClass(node, source, result, currentNamespace, locator);
                break;
            case "interface_declaration":
                ExtractInterface(node, source, result, currentNamespace, locator);
                break;
            case "trait_declaration":
                ExtractTrait(node, source, result, currentNamespace, locator);
                break;
            case "enum_declaration":
                ExtractEnum(node, source, result, currentNamespace, locator);
                break;
            case "function_definition":
                ExtractFunction(node, source, result, currentNamespace, locator);
                break;
        }

        // Recursively process children
        foreach (var child in node.Children)
        {
            ExtractFromNode(child, source, result, currentNamespace, locator);
        }
    }

    private string? ExtractNamespace(Node node, string source, PHPParseResult result, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "namespace_name");
        if (nameNode is null) return null;

        var name = nameNode.Text;
        var span = locator.FindSpan(node.Text);
        result.Namespace = name;
        result.NamespaceSpan = span;
        return name;
    }

    private void ExtractUseStatements(Node node, string source, PHPParseResult result, SpanLocator locator)
    {
        foreach (var clause in FindChildrenByType(node, "namespace_use_clause"))
        {
            var nameNode = FindChildByType(clause, "qualified_name") ?? FindChildByType(clause, "namespace_name");
            if (nameNode is null) continue;

            var name = nameNode.Text;
            var aliasNode = FindChildByType(clause, "namespace_aliasing_clause");
            string? alias = null;
            if (aliasNode is not null)
            {
                var aliasNameNode = FindChildByType(aliasNode, "name");
                if (aliasNameNode is not null)
                    alias = aliasNameNode.Text;
            }

            result.UseStatements.Add(new PHPUseInfo
            {
                Name = name,
                Alias = alias,
                Span = locator.FindSpan(clause.Text)
            });
        }
    }

    private void ExtractClass(Node node, string source, PHPParseResult result, string? ns, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var name = nameNode.Text;
        var classInfo = new PHPClassInfo
        {
            Name = name,
            Namespace = ns,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(name, node.Text)
        };

        // Check modifiers
        foreach (var child in node.Children)
        {
            switch (child.Type)
            {
                case "visibility_modifier":
                    classInfo.Accessibility = child.Text.ToLowerInvariant();
                    break;
                case "abstract_modifier":
                    classInfo.IsAbstract = true;
                    break;
                case "final_modifier":
                    classInfo.IsFinal = true;
                    break;
                case "readonly_modifier":
                    classInfo.IsReadonly = true;
                    break;
            }
        }

        // Extract base class
        var baseClause = FindChildByType(node, "base_clause");
        if (baseClause is not null)
        {
            var baseNameNode = FindChildByType(baseClause, "name") ?? FindChildByType(baseClause, "qualified_name");
            if (baseNameNode is not null)
                classInfo.Extends = baseNameNode.Text;
        }

        // Extract interfaces
        var interfacesClause = FindChildByType(node, "class_interface_clause");
        if (interfacesClause is not null)
        {
            var interfaceNames = FindChildrenByType(interfacesClause, "name")
                .Concat(FindChildrenByType(interfacesClause, "qualified_name"));
            foreach (var iface in interfaceNames)
            {
                classInfo.Implements.Add(iface.Text);
            }
        }

        // Extract class body members
        var body = FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            ExtractClassMembers(body, source, classInfo, locator);
        }

        result.Classes.Add(classInfo);
    }

    private void ExtractClassMembers(Node bodyNode, string source, PHPClassInfo classInfo, SpanLocator locator)
    {
        foreach (var member in bodyNode.Children)
        {
            switch (member.Type)
            {
                case "method_declaration":
                    ExtractMethod(member, source, classInfo, locator);
                    break;
                case "property_declaration":
                    ExtractProperty(member, source, classInfo, locator);
                    break;
                case "const_declaration":
                    ExtractConstant(member, source, classInfo, locator);
                    break;
                case "use_declaration":
                    ExtractTraitUse(member, source, classInfo);
                    break;
            }
        }
    }

    private void ExtractMethod(Node node, string source, PHPClassInfo classInfo, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var methodName = nameNode.Text;
        var method = new PHPMethodInfo
        {
            Name = methodName,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(methodName, node.Text)
        };

        // Extract modifiers
        foreach (var child in node.Children)
        {
            switch (child.Type)
            {
                case "visibility_modifier":
                    method.Accessibility = child.Text.ToLowerInvariant();
                    break;
                case "static_modifier":
                    method.IsStatic = true;
                    break;
                case "abstract_modifier":
                    method.IsAbstract = true;
                    break;
                case "final_modifier":
                    method.IsFinal = true;
                    break;
            }
        }

        // Extract return type
        var returnType = FindChildByType(node, "return_type");
        if (returnType is not null)
        {
            method.ReturnType = returnType.Text.TrimStart(':').Trim();
        }

        // Extract parameters
        var paramList = FindChildByType(node, "formal_parameters");
        if (paramList is not null)
        {
            ExtractParameters(paramList, method);
        }

        classInfo.Methods.Add(method);
    }

    private void ExtractParameters(Node paramList, PHPMethodInfo method)
    {
        var paramNodes = FindChildrenByType(paramList, "simple_parameter")
            .Concat(FindChildrenByType(paramList, "variadic_parameter"))
            .Concat(FindChildrenByType(paramList, "property_promotion_parameter"));

        foreach (var param in paramNodes)
        {
            var varNode = FindChildByType(param, "variable_name");
            if (varNode is null) continue;

            var paramName = varNode.Text;
            var typeNode = FindChildByType(param, "type_list") ?? FindChildByType(param, "named_type");
            var paramType = typeNode?.Text;

            method.Parameters.Add($"{paramType ?? ""} {paramName}".Trim());
        }
    }

    private void ExtractProperty(Node node, string source, PHPClassInfo classInfo, SpanLocator locator)
    {
        foreach (var propElement in FindChildrenByType(node, "property_element"))
        {
            var varNode = FindChildByType(propElement, "variable_name");
            if (varNode is null) continue;

            var propName = varNode.Text;
            var prop = new PHPPropertyInfo
            {
                Name = propName,
                Span = locator.FindSpan(node.Text),
                NameSpan = locator.FindSpan(propName, node.Text)
            };

            // Extract modifiers
            foreach (var child in node.Children)
            {
                switch (child.Type)
                {
                    case "visibility_modifier":
                        prop.Accessibility = child.Text.ToLowerInvariant();
                        break;
                    case "static_modifier":
                        prop.IsStatic = true;
                        break;
                    case "readonly_modifier":
                        prop.IsReadonly = true;
                        break;
                }
            }

            // Extract type
            var typeNode = FindChildByType(node, "type_list") ?? FindChildByType(node, "named_type");
            if (typeNode is not null)
            {
                prop.Type = typeNode.Text;
            }

            // Check for default value
            var defaultValue = FindChildByType(propElement, "property_initializer");
            prop.HasDefault = defaultValue is not null;

            classInfo.Properties.Add(prop);
        }
    }

    private void ExtractConstant(Node node, string source, PHPClassInfo classInfo, SpanLocator locator)
    {
        foreach (var constElement in FindChildrenByType(node, "const_element"))
        {
            var nameNode = FindChildByType(constElement, "name");
            if (nameNode is null) continue;

            var constName = nameNode.Text;
            var constant = new PHPConstantInfo
            {
                Name = constName,
                Span = locator.FindSpan(node.Text),
                NameSpan = locator.FindSpan(constName, node.Text)
            };

            // Extract visibility
            foreach (var child in node.Children)
            {
                if (child.Type == "visibility_modifier")
                {
                    constant.Accessibility = child.Text.ToLowerInvariant();
                }
            }

            classInfo.Constants.Add(constant);
        }
    }

    private void ExtractTraitUse(Node node, string source, PHPClassInfo classInfo)
    {
        var traitNames = FindChildrenByType(node, "name")
            .Concat(FindChildrenByType(node, "qualified_name"));

        foreach (var traitNode in traitNames)
        {
            classInfo.UsesTraits.Add(traitNode.Text);
        }
    }

    private void ExtractInterface(Node node, string source, PHPParseResult result, string? ns, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var name = nameNode.Text;
        var iface = new PHPInterfaceInfo
        {
            Name = name,
            Namespace = ns,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(name, node.Text)
        };

        // Extract extended interfaces
        var baseClause = FindChildByType(node, "base_clause");
        if (baseClause is not null)
        {
            var baseNames = FindChildrenByType(baseClause, "name")
                .Concat(FindChildrenByType(baseClause, "qualified_name"));
            foreach (var baseName in baseNames)
            {
                iface.Extends.Add(baseName.Text);
            }
        }

        // Extract methods
        var body = FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            foreach (var member in body.Children)
            {
                if (member.Type == "method_declaration")
                {
                    var methodNameNode = FindChildByType(member, "name");
                    if (methodNameNode is null) continue;

                    var methodName = methodNameNode.Text;
                    var method = new PHPMethodInfo
                    {
                        Name = methodName,
                        Accessibility = "public",
                        Span = locator.FindSpan(member.Text),
                        NameSpan = locator.FindSpan(methodName, member.Text)
                    };

                    var returnType = FindChildByType(member, "return_type");
                    if (returnType is not null)
                        method.ReturnType = returnType.Text.TrimStart(':').Trim();

                    var paramList = FindChildByType(member, "formal_parameters");
                    if (paramList is not null)
                        ExtractParameters(paramList, method);

                    iface.Methods.Add(method);
                }
                else if (member.Type == "const_declaration")
                {
                    foreach (var constElement in FindChildrenByType(member, "const_element"))
                    {
                        var constNameNode = FindChildByType(constElement, "name");
                        if (constNameNode is null) continue;

                        iface.Constants.Add(new PHPConstantInfo
                        {
                            Name = constNameNode.Text,
                            Accessibility = "public",
                            Span = locator.FindSpan(member.Text),
                            NameSpan = locator.FindSpan(constNameNode.Text, member.Text)
                        });
                    }
                }
            }
        }

        result.Interfaces.Add(iface);
    }

    private void ExtractTrait(Node node, string source, PHPParseResult result, string? ns, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var name = nameNode.Text;
        var trait = new PHPTraitInfo
        {
            Name = name,
            Namespace = ns,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(name, node.Text)
        };

        var body = FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            foreach (var member in body.Children)
            {
                if (member.Type == "method_declaration")
                {
                    var methodNameNode = FindChildByType(member, "name");
                    if (methodNameNode is null) continue;

                    var methodName = methodNameNode.Text;
                    var method = new PHPMethodInfo
                    {
                        Name = methodName,
                        Span = locator.FindSpan(member.Text),
                        NameSpan = locator.FindSpan(methodName, member.Text)
                    };

                    foreach (var child in member.Children)
                    {
                        switch (child.Type)
                        {
                            case "visibility_modifier":
                                method.Accessibility = child.Text.ToLowerInvariant();
                                break;
                            case "static_modifier":
                                method.IsStatic = true;
                                break;
                            case "abstract_modifier":
                                method.IsAbstract = true;
                                break;
                        }
                    }

                    var returnType = FindChildByType(member, "return_type");
                    if (returnType is not null)
                        method.ReturnType = returnType.Text.TrimStart(':').Trim();

                    var paramList = FindChildByType(member, "formal_parameters");
                    if (paramList is not null)
                        ExtractParameters(paramList, method);

                    trait.Methods.Add(method);
                }
                else if (member.Type == "property_declaration")
                {
                    foreach (var propElement in FindChildrenByType(member, "property_element"))
                    {
                        var varNode = FindChildByType(propElement, "variable_name");
                        if (varNode is null) continue;

                        var prop = new PHPPropertyInfo
                        {
                            Name = varNode.Text,
                            Span = locator.FindSpan(member.Text),
                            NameSpan = locator.FindSpan(varNode.Text, member.Text)
                        };

                        foreach (var child in member.Children)
                        {
                            if (child.Type == "visibility_modifier")
                                prop.Accessibility = child.Text.ToLowerInvariant();
                            else if (child.Type == "static_modifier")
                                prop.IsStatic = true;
                        }

                        trait.Properties.Add(prop);
                    }
                }
            }
        }

        result.Traits.Add(trait);
    }

    private void ExtractEnum(Node node, string source, PHPParseResult result, string? ns, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var name = nameNode.Text;
        var enumInfo = new PHPEnumInfo
        {
            Name = name,
            Namespace = ns,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(name, node.Text)
        };

        // Look for backed type
        foreach (var child in node.Children)
        {
            if (child.Type is "named_type" or "primitive_type")
            {
                enumInfo.BackedType = child.Text;
            }
        }

        // Extract interfaces
        var interfacesClause = FindChildByType(node, "class_interface_clause");
        if (interfacesClause is not null)
        {
            var interfaceNames = FindChildrenByType(interfacesClause, "name")
                .Concat(FindChildrenByType(interfacesClause, "qualified_name"));
            foreach (var iface in interfaceNames)
            {
                enumInfo.Implements.Add(iface.Text);
            }
        }

        // Extract cases and methods
        var body = FindChildByType(node, "enum_declaration_list");
        if (body is not null)
        {
            foreach (var member in body.Children)
            {
                if (member.Type == "enum_case")
                {
                    var caseNameNode = FindChildByType(member, "name");
                    if (caseNameNode is not null)
                    {
                        enumInfo.Cases.Add(new PHPEnumCaseInfo
                        {
                            Name = caseNameNode.Text,
                            Span = locator.FindSpan(member.Text),
                            NameSpan = locator.FindSpan(caseNameNode.Text, member.Text)
                        });
                    }
                }
                else if (member.Type == "method_declaration")
                {
                    var methodNameNode = FindChildByType(member, "name");
                    if (methodNameNode is null) continue;

                    var methodName = methodNameNode.Text;
                    var method = new PHPMethodInfo
                    {
                        Name = methodName,
                        Span = locator.FindSpan(member.Text),
                        NameSpan = locator.FindSpan(methodName, member.Text)
                    };

                    foreach (var child in member.Children)
                    {
                        if (child.Type == "visibility_modifier")
                            method.Accessibility = child.Text.ToLowerInvariant();
                        else if (child.Type == "static_modifier")
                            method.IsStatic = true;
                    }

                    var returnType = FindChildByType(member, "return_type");
                    if (returnType is not null)
                        method.ReturnType = returnType.Text.TrimStart(':').Trim();

                    enumInfo.Methods.Add(method);
                }
            }
        }

        result.Enums.Add(enumInfo);
    }

    private void ExtractFunction(Node node, string source, PHPParseResult result, string? ns, SpanLocator locator)
    {
        var nameNode = FindChildByType(node, "name");
        if (nameNode is null) return;

        var name = nameNode.Text;
        var func = new PHPFunctionInfo
        {
            Name = name,
            Namespace = ns,
            Span = locator.FindSpan(node.Text),
            NameSpan = locator.FindSpan(name, node.Text)
        };

        var returnType = FindChildByType(node, "return_type");
        if (returnType is not null)
        {
            func.ReturnType = returnType.Text.TrimStart(':').Trim();
        }

        var paramList = FindChildByType(node, "formal_parameters");
        if (paramList is not null)
        {
            var paramNodes = FindChildrenByType(paramList, "simple_parameter")
                .Concat(FindChildrenByType(paramList, "variadic_parameter"));

            foreach (var param in paramNodes)
            {
                var varNode = FindChildByType(param, "variable_name");
                if (varNode is null) continue;

                var paramName = varNode.Text;
                var typeNode = FindChildByType(param, "type_list") ?? FindChildByType(param, "named_type");
                var paramType = typeNode?.Text;

                func.Parameters.Add($"{paramType ?? ""} {paramName}".Trim());
            }
        }

        result.Functions.Add(func);
    }

    private static Node? FindChildByType(Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type)
                return child;
        }
        return null;
    }

    private static IEnumerable<Node> FindChildrenByType(Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type)
                yield return child;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.Dispose();
        _language.Dispose();
    }
}

/// <summary>
/// Locates text spans within source code by searching for content.
/// Tracks position to handle sequential searches correctly.
/// </summary>
internal sealed class SpanLocator
{
    private readonly string _source;
    private int _lastPosition;

    public SpanLocator(string source)
    {
        _source = source;
        _lastPosition = 0;
    }

    /// <summary>
    /// Find the span of the given text, searching from the last found position.
    /// </summary>
    public PHPSpan FindSpan(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new PHPSpan(0, 0);

        var index = _source.IndexOf(text, _lastPosition, StringComparison.Ordinal);
        if (index < 0)
        {
            // Try from beginning if not found after last position
            index = _source.IndexOf(text, StringComparison.Ordinal);
        }

        if (index >= 0)
        {
            _lastPosition = index;
            return new PHPSpan(index, index + text.Length);
        }

        return new PHPSpan(0, 0);
    }

    /// <summary>
    /// Find a substring within a containing context.
    /// </summary>
    public PHPSpan FindSpan(string text, string context)
    {
        // First find the context
        var contextSpan = FindSpan(context);
        if (contextSpan.Start == 0 && contextSpan.End == 0 && !string.IsNullOrEmpty(context))
            return contextSpan;

        // Then find the text within that context
        var contextStart = contextSpan.Start;
        var index = _source.IndexOf(text, contextStart, Math.Min(context.Length, _source.Length - contextStart), StringComparison.Ordinal);

        if (index >= 0)
        {
            return new PHPSpan(index, index + text.Length);
        }

        return contextSpan;
    }
}

/// <summary>
/// Result of parsing a PHP file.
/// </summary>
public sealed class PHPParseResult
{
    public string? Namespace { get; set; }
    public PHPSpan? NamespaceSpan { get; set; }
    public List<PHPUseInfo> UseStatements { get; } = [];
    public List<PHPClassInfo> Classes { get; } = [];
    public List<PHPInterfaceInfo> Interfaces { get; } = [];
    public List<PHPTraitInfo> Traits { get; } = [];
    public List<PHPEnumInfo> Enums { get; } = [];
    public List<PHPFunctionInfo> Functions { get; } = [];
}

public sealed record PHPSpan(int Start, int End);

public sealed class PHPUseInfo
{
    public required string Name { get; init; }
    public string? Alias { get; init; }
    public required PHPSpan Span { get; init; }
}

public sealed class PHPClassInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public string? Accessibility { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsFinal { get; set; }
    public bool IsReadonly { get; set; }
    public string? Extends { get; set; }
    public List<string> Implements { get; } = [];
    public List<string> UsesTraits { get; } = [];
    public List<PHPMethodInfo> Methods { get; } = [];
    public List<PHPPropertyInfo> Properties { get; } = [];
    public List<PHPConstantInfo> Constants { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPInterfaceInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public List<string> Extends { get; } = [];
    public List<PHPMethodInfo> Methods { get; } = [];
    public List<PHPConstantInfo> Constants { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPTraitInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public List<PHPMethodInfo> Methods { get; } = [];
    public List<PHPPropertyInfo> Properties { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPEnumInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public string? BackedType { get; set; }
    public List<string> Implements { get; } = [];
    public List<PHPEnumCaseInfo> Cases { get; } = [];
    public List<PHPMethodInfo> Methods { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPEnumCaseInfo
{
    public required string Name { get; init; }
    public string? Value { get; init; }
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPFunctionInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public string? ReturnType { get; set; }
    public List<string> Parameters { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPMethodInfo
{
    public required string Name { get; init; }
    public string? Accessibility { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsFinal { get; set; }
    public string? ReturnType { get; set; }
    public List<string> Parameters { get; } = [];
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPPropertyInfo
{
    public required string Name { get; init; }
    public string? Accessibility { get; set; }
    public bool IsStatic { get; set; }
    public bool IsReadonly { get; set; }
    public string? Type { get; set; }
    public bool HasDefault { get; set; }
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}

public sealed class PHPConstantInfo
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public string? Accessibility { get; set; }
    public string? Value { get; init; }
    public required PHPSpan Span { get; init; }
    public required PHPSpan NameSpan { get; init; }
}
