using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace RepoQL.Formats.PHP;

/// <summary>
/// Wrapper around ANTLR4 for parsing PHP source code.
/// Uses ANTLR for structural parsing, then locates spans by text matching.
/// </summary>
public sealed class PHPAntlrClient
{
    /// <summary>
    /// Parse PHP source code and extract semantic information.
    /// </summary>
    public PHPParseResult Parse(string sourceCode)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);

        var inputStream = new AntlrInputStream(sourceCode);
        var lexer = new PhpLexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new PhpParser(tokenStream);

        // Parse the document
        var tree = parser.htmlDocument();

        var result = new PHPParseResult();
        var locator = new SpanLocator(sourceCode);
        var visitor = new PHPVisitor(result, locator, tokenStream);
        visitor.Visit(tree);

        return result;
    }
}

/// <summary>
/// ANTLR visitor that extracts PHP semantic information.
/// </summary>
internal sealed class PHPVisitor : PhpParserBaseVisitor<object?>
{
    private readonly PHPParseResult _result;
    private readonly SpanLocator _locator;
    private readonly CommonTokenStream _tokenStream;
    private string? _currentNamespace;

    public PHPVisitor(PHPParseResult result, SpanLocator locator, CommonTokenStream tokenStream)
    {
        _result = result;
        _locator = locator;
        _tokenStream = tokenStream;
    }

    // Override to ensure the visitor walks the entire tree
    public override object? VisitChildren(IRuleNode node)
    {
        for (int i = 0; i < node.ChildCount; i++)
        {
            Visit(node.GetChild(i));
        }
        return null;
    }

    public override object? VisitNamespaceDeclaration(PhpParser.NamespaceDeclarationContext context)
    {
        var nameList = context.namespaceNameList();
        if (nameList is not null)
        {
            _currentNamespace = nameList.GetText();
            _result.Namespace = _currentNamespace;
            _result.NamespaceSpan = GetSpan(context);
        }
        return base.VisitNamespaceDeclaration(context);
    }

    public override object? VisitUseDeclaration(PhpParser.UseDeclarationContext context)
    {
        var contentList = context.useDeclarationContentList();
        if (contentList is null) return base.VisitUseDeclaration(context);

        foreach (var content in contentList.useDeclarationContent())
        {
            var nameList = content.namespaceNameList();
            if (nameList is null) continue;

            var name = nameList.GetText();
            _result.UseStatements.Add(new PHPUseInfo
            {
                Name = name,
                Alias = null, // TODO: Extract alias if needed
                Span = GetSpan(content)
            });
        }
        return base.VisitUseDeclaration(context);
    }

    public override object? VisitClassDeclaration(PhpParser.ClassDeclarationContext context)
    {
        var identifier = context.identifier();
        if (identifier is null) return base.VisitClassDeclaration(context);

        // Check if this is an interface
        if (context.Interface() is not null)
        {
            return VisitInterfaceFromClassDeclaration(context, identifier);
        }

        var classEntry = context.classEntryType();
        if (classEntry?.Trait() is not null)
        {
            return VisitTraitFromClassDeclaration(context, identifier);
        }

        var name = identifier.GetText();
        var classInfo = new PHPClassInfo
        {
            Name = name,
            Namespace = _currentNamespace,
            Span = GetSpan(context),
            NameSpan = GetSpan(identifier)
        };

        // Check modifiers
        var modifier = context.modifier();
        if (modifier?.Abstract() is not null)
            classInfo.IsAbstract = true;
        if (modifier?.Final() is not null)
            classInfo.IsFinal = true;

        // Extract base class
        var baseRef = context.qualifiedStaticTypeRef();
        if (baseRef is not null)
            classInfo.Extends = baseRef.GetText();

        // Extract interfaces
        var interfaceList = context.interfaceList();
        if (interfaceList is not null)
        {
            foreach (var iface in interfaceList.qualifiedStaticTypeRef())
            {
                classInfo.Implements.Add(iface.GetText());
            }
        }

        // Extract class body members
        foreach (var statement in context.classStatement())
        {
            ExtractClassMember(statement, classInfo);
        }

        _result.Classes.Add(classInfo);
        return null; // Don't recurse further
    }

    private object? VisitInterfaceFromClassDeclaration(PhpParser.ClassDeclarationContext context, PhpParser.IdentifierContext identifier)
    {
        var name = identifier.GetText();
        var iface = new PHPInterfaceInfo
        {
            Name = name,
            Namespace = _currentNamespace,
            Span = GetSpan(context),
            NameSpan = GetSpan(identifier)
        };

        // Extract extended interfaces
        var interfaceList = context.interfaceList();
        if (interfaceList is not null)
        {
            foreach (var extended in interfaceList.qualifiedStaticTypeRef())
            {
                iface.Extends.Add(extended.GetText());
            }
        }

        // Extract members
        foreach (var statement in context.classStatement())
        {
            ExtractInterfaceMember(statement, iface);
        }

        _result.Interfaces.Add(iface);
        return null;
    }

    private object? VisitTraitFromClassDeclaration(PhpParser.ClassDeclarationContext context, PhpParser.IdentifierContext identifier)
    {
        var name = identifier.GetText();
        var trait = new PHPTraitInfo
        {
            Name = name,
            Namespace = _currentNamespace,
            Span = GetSpan(context),
            NameSpan = GetSpan(identifier)
        };

        // Extract members
        foreach (var statement in context.classStatement())
        {
            ExtractTraitMember(statement, trait);
        }

        _result.Traits.Add(trait);
        return null;
    }

    private void ExtractClassMember(PhpParser.ClassStatementContext statement, PHPClassInfo classInfo)
    {
        // Check for method
        var funcKeyword = statement.Function_();
        if (funcKeyword is not null)
        {
            var methodId = statement.identifier();
            if (methodId is not null)
            {
                var method = ExtractMethod(statement, methodId);
                classInfo.Methods.Add(method);
            }
            return;
        }

        // Check for property (has variableInitializer but no Function_)
        var propModifiers = statement.propertyModifiers();
        if (propModifiers is not null)
        {
            foreach (var varInit in statement.variableInitializer())
            {
                var varName = varInit.VarName();
                if (varName is null) continue;

                var prop = new PHPPropertyInfo
                {
                    Name = varName.GetText(),
                    Span = GetSpan(statement),
                    NameSpan = _locator.FindSpan(varName.GetText(), statement.GetText())
                };

                // Extract modifiers from propertyModifiers
                var memberMods = propModifiers.memberModifiers();
                if (memberMods is not null)
                {
                    ExtractMemberModifiers(memberMods, out var accessibility, out var isStatic, out _, out _, out var isReadonly);
                    prop.Accessibility = accessibility;
                    prop.IsStatic = isStatic;
                    prop.IsReadonly = isReadonly;
                }
                else if (propModifiers.Var() is not null)
                {
                    prop.Accessibility = "public";
                }

                // Extract type
                var typeHint = statement.typeHint();
                if (typeHint is not null)
                    prop.Type = typeHint.GetText();

                // Check for default value
                prop.HasDefault = varInit.constantInitializer() is not null;

                classInfo.Properties.Add(prop);
            }
            return;
        }

        // Check for constant
        if (statement.Const() is not null)
        {
            foreach (var constInit in statement.identifierInitializer())
            {
                var constId = constInit.identifier();
                if (constId is null) continue;

                var constant = new PHPConstantInfo
                {
                    Name = constId.GetText(),
                    Span = GetSpan(statement),
                    NameSpan = GetSpan(constId)
                };

                var memberMods = statement.memberModifiers();
                if (memberMods is not null)
                {
                    ExtractMemberModifiers(memberMods, out var accessibility, out _, out _, out _, out _);
                    constant.Accessibility = accessibility;
                }

                classInfo.Constants.Add(constant);
            }
            return;
        }

        // Check for trait use
        var use = statement.Use();
        if (use is not null)
        {
            var traitNames = statement.qualifiedNamespaceNameList();
            if (traitNames is not null)
            {
                foreach (var traitName in traitNames.qualifiedNamespaceName())
                {
                    classInfo.UsesTraits.Add(traitName.GetText());
                }
            }
        }
    }

    private void ExtractInterfaceMember(PhpParser.ClassStatementContext statement, PHPInterfaceInfo iface)
    {
        var funcKeyword = statement.Function_();
        if (funcKeyword is not null)
        {
            var methodId = statement.identifier();
            if (methodId is not null)
            {
                var method = ExtractMethod(statement, methodId);
                method.Accessibility = "public";
                iface.Methods.Add(method);
            }
            return;
        }

        if (statement.Const() is not null)
        {
            foreach (var constInit in statement.identifierInitializer())
            {
                var constId = constInit.identifier();
                if (constId is null) continue;

                iface.Constants.Add(new PHPConstantInfo
                {
                    Name = constId.GetText(),
                    Accessibility = "public",
                    Span = GetSpan(statement),
                    NameSpan = GetSpan(constId)
                });
            }
        }
    }

    private void ExtractTraitMember(PhpParser.ClassStatementContext statement, PHPTraitInfo trait)
    {
        var funcKeyword = statement.Function_();
        if (funcKeyword is not null)
        {
            var methodId = statement.identifier();
            if (methodId is not null)
            {
                var method = ExtractMethod(statement, methodId);
                trait.Methods.Add(method);
            }
            return;
        }

        var propModifiers = statement.propertyModifiers();
        if (propModifiers is not null)
        {
            foreach (var varInit in statement.variableInitializer())
            {
                var varName = varInit.VarName();
                if (varName is null) continue;

                var prop = new PHPPropertyInfo
                {
                    Name = varName.GetText(),
                    Span = GetSpan(statement),
                    NameSpan = _locator.FindSpan(varName.GetText(), statement.GetText())
                };

                var memberMods = propModifiers.memberModifiers();
                if (memberMods is not null)
                {
                    ExtractMemberModifiers(memberMods, out var accessibility, out var isStatic, out _, out _, out var isReadonly);
                    prop.Accessibility = accessibility;
                    prop.IsStatic = isStatic;
                    prop.IsReadonly = isReadonly;
                }

                trait.Properties.Add(prop);
            }
        }
    }

    private PHPMethodInfo ExtractMethod(PhpParser.ClassStatementContext statement, PhpParser.IdentifierContext methodId)
    {
        var methodName = methodId.GetText();
        var method = new PHPMethodInfo
        {
            Name = methodName,
            Span = GetSpan(statement),
            NameSpan = GetSpan(methodId)
        };

        // Extract modifiers
        var memberMods = statement.memberModifiers();
        if (memberMods is not null)
        {
            ExtractMemberModifiers(memberMods, out var accessibility, out var isStatic, out var isAbstract, out var isFinal, out _);
            method.Accessibility = accessibility;
            method.IsStatic = isStatic;
            method.IsAbstract = isAbstract;
            method.IsFinal = isFinal;
        }

        // Extract return type
        var returnTypeDecl = statement.returnTypeDecl();
        if (returnTypeDecl?.typeHint() is not null)
        {
            method.ReturnType = returnTypeDecl.typeHint().GetText();
        }

        // Extract parameters
        var paramList = statement.formalParameterList();
        if (paramList is not null)
        {
            ExtractParameters(paramList, method);
        }

        return method;
    }

    private void ExtractMemberModifiers(
        PhpParser.MemberModifiersContext modifiers,
        out string? accessibility,
        out bool isStatic,
        out bool isAbstract,
        out bool isFinal,
        out bool isReadonly)
    {
        accessibility = null;
        isStatic = false;
        isAbstract = false;
        isFinal = false;
        isReadonly = false;

        foreach (var mod in modifiers.memberModifier())
        {
            if (mod.Public() is not null) accessibility = "public";
            else if (mod.Protected() is not null) accessibility = "protected";
            else if (mod.Private() is not null) accessibility = "private";
            else if (mod.Static() is not null) isStatic = true;
            else if (mod.Abstract() is not null) isAbstract = true;
            else if (mod.Final() is not null) isFinal = true;
            else if (mod.Readonly() is not null) isReadonly = true;
        }
    }

    private void ExtractParameters(PhpParser.FormalParameterListContext paramList, PHPMethodInfo method)
    {
        foreach (var param in paramList.formalParameter())
        {
            var varInit = param.variableInitializer();
            if (varInit is null) continue;

            var varName = varInit.VarName();
            if (varName is null) continue;

            var paramName = varName.GetText();
            var typeHint = param.typeHint();
            var paramType = typeHint?.GetText();

            method.Parameters.Add($"{paramType ?? ""} {paramName}".Trim());
        }
    }

    public override object? VisitFunctionDeclaration(PhpParser.FunctionDeclarationContext context)
    {
        var identifier = context.identifier();
        if (identifier is null) return base.VisitFunctionDeclaration(context);

        var name = identifier.GetText();
        var func = new PHPFunctionInfo
        {
            Name = name,
            Namespace = _currentNamespace,
            Span = GetSpan(context),
            NameSpan = GetSpan(identifier)
        };

        // Extract return type
        var typeHint = context.typeHint();
        if (typeHint is not null)
        {
            func.ReturnType = typeHint.GetText();
        }

        // Extract parameters
        var paramList = context.formalParameterList();
        if (paramList is not null)
        {
            foreach (var param in paramList.formalParameter())
            {
                var varInit = param.variableInitializer();
                if (varInit is null) continue;

                var varName = varInit.VarName();
                if (varName is null) continue;

                var paramName = varName.GetText();
                var paramTypeHint = param.typeHint();
                var paramType = paramTypeHint?.GetText();

                func.Parameters.Add($"{paramType ?? ""} {paramName}".Trim());
            }
        }

        _result.Functions.Add(func);
        return null; // Don't recurse further
    }

    public override object? VisitEnumDeclaration(PhpParser.EnumDeclarationContext context)
    {
        var identifier = context.identifier();
        if (identifier is null) return base.VisitEnumDeclaration(context);

        var name = identifier.GetText();
        var enumInfo = new PHPEnumInfo
        {
            Name = name,
            Namespace = _currentNamespace,
            Span = GetSpan(context),
            NameSpan = GetSpan(identifier)
        };

        // Extract backed type
        if (context.IntType() is not null)
            enumInfo.BackedType = "int";
        else if (context.StringType() is not null)
            enumInfo.BackedType = "string";

        // Extract interfaces
        var interfaceList = context.interfaceList();
        if (interfaceList is not null)
        {
            foreach (var iface in interfaceList.qualifiedStaticTypeRef())
            {
                enumInfo.Implements.Add(iface.GetText());
            }
        }

        // Extract cases and methods
        foreach (var item in context.enumItem())
        {
            var caseKeyword = item.Case();
            if (caseKeyword is not null)
            {
                var caseId = item.identifier();
                if (caseId is not null)
                {
                    enumInfo.Cases.Add(new PHPEnumCaseInfo
                    {
                        Name = caseId.GetText(),
                        Span = GetSpan(item),
                        NameSpan = GetSpan(caseId)
                    });
                }
            }
            else
            {
                // Method in enum
                var funcDecl = item.functionDeclaration();
                if (funcDecl is not null)
                {
                    var methodId = funcDecl.identifier();
                    if (methodId is not null)
                    {
                        var method = new PHPMethodInfo
                        {
                            Name = methodId.GetText(),
                            Span = GetSpan(funcDecl),
                            NameSpan = GetSpan(methodId)
                        };

                        var memberMods = item.memberModifiers();
                        if (memberMods is not null)
                        {
                            ExtractMemberModifiers(memberMods, out var accessibility, out var isStatic, out _, out _, out _);
                            method.Accessibility = accessibility;
                            method.IsStatic = isStatic;
                        }

                        var typeHint = funcDecl.typeHint();
                        if (typeHint is not null)
                            method.ReturnType = typeHint.GetText();

                        enumInfo.Methods.Add(method);
                    }
                }
            }
        }

        _result.Enums.Add(enumInfo);
        return null; // Don't recurse further
    }

    private PHPSpan GetSpan(ParserRuleContext context)
    {
        var start = context.Start;
        var stop = context.Stop ?? start;
        return new PHPSpan(start.StartIndex, stop.StopIndex + 1);
    }

    private PHPSpan GetSpan(ITerminalNode node)
    {
        var symbol = node.Symbol;
        return new PHPSpan(symbol.StartIndex, symbol.StopIndex + 1);
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
