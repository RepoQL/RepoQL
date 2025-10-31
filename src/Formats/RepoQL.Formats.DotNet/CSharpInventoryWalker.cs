using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal sealed class CSharpInventoryWalker : CSharpSyntaxWalker
{
    private readonly Guid _documentId;
    private readonly TextLineMap _lineMap;
    private readonly Stack<NamespaceContext> _namespaceStack = new();
    private readonly Stack<TypeContext> _typeStack = new();
    private readonly Dictionary<Guid, BaseNamespaceDeclarationSyntax> _namespaceDeclarations = new();
    private readonly Dictionary<Guid, BaseTypeDeclarationSyntax> _typeDeclarations = new();
    private readonly Dictionary<Guid, SyntaxNode> _memberDeclarations = new();
    private readonly Dictionary<SyntaxNode, Guid> _declaredNodeIds = new(ReferenceEqualityComparer.Instance);

    public CSharpInventoryWalker(Guid documentId, TextLineMap lineMap)
        : base(SyntaxWalkerDepth.StructuredTrivia)
    {
        _documentId = documentId;
        _lineMap = lineMap ?? throw new ArgumentNullException(nameof(lineMap));
    }

    public List<CSharpNamespaceInfo> Namespaces { get; } = new();
    public List<CSharpTypeInfo> Types { get; } = new();
    public List<CSharpMemberInfo> Members { get; } = new();
    public List<CSharpUsingInfo> Usings { get; } = new();
    public IReadOnlyDictionary<Guid, BaseTypeDeclarationSyntax> TypeDeclarations => _typeDeclarations;
    public IReadOnlyDictionary<Guid, SyntaxNode> MemberDeclarations => _memberDeclarations;
    public IReadOnlyDictionary<Guid, BaseNamespaceDeclarationSyntax> NamespaceDeclarations => _namespaceDeclarations;
    public IReadOnlyDictionary<SyntaxNode, Guid> DeclaredNodeIds => _declaredNodeIds;

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var span = ToDocumentSpan(node.Span);
        var nodeId = CreateNodeId("using", node.Span);
        var spanId = CreateSpanId("using", node.Span);
        Usings.Add(new CSharpUsingInfo(
            NodeId: nodeId,
            SpanId: spanId,
            Name: node.Name?.ToString() ?? string.Empty,
            Alias: node.Alias?.Name.Identifier.ValueText,
            IsStatic: node.StaticKeyword.Kind() == SyntaxKind.StaticKeyword,
            Span: span));
        base.VisitUsingDirective(node);
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        using var scope = EnterNamespace(node, node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
    }

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        using var scope = EnterNamespace(node, node.Name.ToString());
        base.VisitNamespaceDeclaration(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        using var scope = EnterType(node, "class");
        base.VisitClassDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        using var scope = EnterType(node, "struct");
        base.VisitStructDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        using var scope = EnterType(node, "record");
        base.VisitRecordDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        using var scope = EnterType(node, "interface");
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        using var scope = EnterType(node, "enum");
        base.VisitEnumDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        AddMember(node,
            kind: "method",
            name: node.Identifier.ValueText,
            modifiers: node.Modifiers,
            returnType: node.ReturnType,
            parameterList: node.ParameterList,
            isAsync: node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)));
        base.VisitMethodDeclaration(node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        AddMember(node,
            kind: "constructor",
            name: node.Identifier.ValueText,
            modifiers: node.Modifiers,
            returnType: null,
            parameterList: node.ParameterList,
            isAsync: node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)));
        base.VisitConstructorDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        AddMember(node,
            kind: "property",
            name: node.Identifier.ValueText,
            modifiers: node.Modifiers,
            returnType: node.Type,
            parameterList: null,
            isAsync: false);
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        AddMember(node,
            kind: "indexer",
            name: node.ThisKeyword.Text,
            modifiers: node.Modifiers,
            returnType: node.Type,
            parameterList: node.ParameterList,
            isAsync: false);
        base.VisitIndexerDeclaration(node);
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        AddMember(node,
            kind: "event",
            name: node.Identifier.ValueText,
            modifiers: node.Modifiers,
            returnType: node.Type,
            parameterList: null,
            isAsync: false);
        base.VisitEventDeclaration(node);
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            AddFieldMember(variable, node.Declaration.Type.ToString(), node.Modifiers, "event");
        }
        base.VisitEventFieldDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            AddFieldMember(variable, node.Declaration.Type.ToString(), node.Modifiers, "field");
        }
        base.VisitFieldDeclaration(node);
    }

    private IDisposable EnterNamespace(SyntaxNode node, string name)
    {
        NamespaceContext? parent = _namespaceStack.Count == 0 ? null : _namespaceStack.Peek();
        var qualified = string.IsNullOrWhiteSpace(parent?.QualifiedName)
            ? name
            : $"{parent!.QualifiedName}.{name}";
        var nsId = CreateNodeId("namespace", node.Span);
        var spanId = CreateSpanId("namespace", node.Span);
        var info = new CSharpNamespaceInfo(
            NodeId: nsId,
            SpanId: spanId,
            ParentNamespaceId: parent?.NodeId,
            Name: name,
            QualifiedName: qualified,
            Span: ToDocumentSpan(node.Span));
        Namespaces.Add(info);
        if (node is BaseNamespaceDeclarationSyntax baseNs)
        {
            _namespaceDeclarations[nsId] = baseNs;
            _declaredNodeIds[baseNs] = nsId;
        }
        _namespaceStack.Push(new NamespaceContext(nsId, qualified));
        return new Scope(() =>
        {
            if (_namespaceStack.Count > 0)
                _namespaceStack.Pop();
        });
    }

    private IDisposable EnterType(BaseTypeDeclarationSyntax node, string kind)
    {
        NamespaceContext? namespaceContext = _namespaceStack.Count == 0 ? null : _namespaceStack.Peek();
        TypeContext? parentType = _typeStack.Count == 0 ? null : _typeStack.Peek();
        var typeName = node switch
        {
            EnumDeclarationSyntax enumDecl => enumDecl.Identifier.ValueText,
            RecordDeclarationSyntax recordDecl => recordDecl.Identifier.ValueText,
            _ => node.Identifier.ValueText
        };
        var containing = parentType?.QualifiedName ?? namespaceContext?.QualifiedName;
        var qualifiedName = string.IsNullOrWhiteSpace(containing) ? typeName : $"{containing}.{typeName}";
        var span = ToDocumentSpan(node.Span);
        var typeNodeId = CreateNodeId("type", node.Span);
        var typeSpanId = CreateSpanId("type", node.Span);
        var (baseType, interfaces) = ExtractInheritance(node);
        var typeInfo = new CSharpTypeInfo(
            NodeId: typeNodeId,
            SpanId: typeSpanId,
            NamespaceNodeId: namespaceContext?.NodeId,
            ParentTypeId: parentType?.NodeId,
            Name: typeName,
            QualifiedName: qualifiedName,
            Kind: kind,
            Accessibility: ResolveAccessibility(node.Modifiers, parentType is null ? "internal" : "private"),
            Namespace: namespaceContext?.QualifiedName,
            IsPartial: node.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            IsStatic: node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            IsRecord: node is RecordDeclarationSyntax,
            BaseType: baseType,
            Interfaces: interfaces,
            Span: span);
        Types.Add(typeInfo);
        _typeDeclarations[typeInfo.NodeId] = node;
        _declaredNodeIds[node] = typeInfo.NodeId;
        _typeStack.Push(new TypeContext(typeInfo.NodeId, qualifiedName));
        return new Scope(() =>
        {
            if (_typeStack.Count > 0)
                _typeStack.Pop();
        });
    }

    private void AddFieldMember(VariableDeclaratorSyntax variable, string typeName, SyntaxTokenList modifiers, string kind)
    {
        if (_typeStack.Count == 0) return;
        var declaring = _typeStack.Peek();
        var span = ToDocumentSpan(variable.Span);
        var memberId = CreateNodeId("member", variable.Span);
        var spanId = CreateSpanId("member", variable.Span);
        var memberInfo = new CSharpMemberInfo(
            NodeId: memberId,
            SpanId: spanId,
            DeclaringTypeId: declaring.NodeId,
            Name: variable.Identifier.ValueText,
            Kind: kind,
            Accessibility: ResolveAccessibility(modifiers, "private"),
            IsStatic: modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            IsAsync: false,
            ReturnType: typeName,
            DeclaringTypeDisplay: declaring.QualifiedName,
            Parameters: Array.Empty<CSharpParameterInfo>(),
            Span: span);
        Members.Add(memberInfo);
        _memberDeclarations[memberId] = variable;
        _declaredNodeIds[variable] = memberId;
    }

    private void AddMember(SyntaxNode node, string kind, string name, SyntaxTokenList modifiers, TypeSyntax? returnType, BaseParameterListSyntax? parameterList, bool isAsync)
    {
        if (_typeStack.Count == 0) return;
        var declaring = _typeStack.Peek();
        var span = ToDocumentSpan(node.Span);
        var memberId = CreateNodeId("member", node.Span);
        var spanId = CreateSpanId("member", node.Span);
        var memberInfo = new CSharpMemberInfo(
            NodeId: memberId,
            SpanId: spanId,
            DeclaringTypeId: declaring.NodeId,
            Name: name,
            Kind: kind,
            Accessibility: ResolveAccessibility(modifiers, "private"),
            IsStatic: modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            IsAsync: isAsync,
            ReturnType: returnType?.ToString(),
            DeclaringTypeDisplay: declaring.QualifiedName,
            Parameters: BuildParameters(parameterList),
            Span: span);
        Members.Add(memberInfo);
        _memberDeclarations[memberId] = node;
        _declaredNodeIds[node] = memberId;
    }

    private IReadOnlyList<CSharpParameterInfo> BuildParameters(BaseParameterListSyntax? parameterList)
    {
        if (parameterList is null)
            return Array.Empty<CSharpParameterInfo>();

        if (parameterList is ParameterListSyntax pl && pl.Parameters.Count > 0)
        {
            var result = new List<CSharpParameterInfo>(pl.Parameters.Count);
            foreach (var parameter in pl.Parameters)
            {
                var type = parameter.Type?.ToString() ?? "object";
                result.Add(new CSharpParameterInfo(
                    parameter.Identifier.ValueText,
                    type,
                    parameter.Default is not null));
            }
            return result;
        }

        if (parameterList is BracketedParameterListSyntax bpl && bpl.Parameters.Count > 0)
        {
            var result = new List<CSharpParameterInfo>(bpl.Parameters.Count);
            foreach (var parameter in bpl.Parameters)
            {
                var type = parameter.Type?.ToString() ?? "object";
                result.Add(new CSharpParameterInfo(
                    parameter.Identifier.ValueText,
                    type,
                    parameter.Default is not null));
            }
            return result;
        }

        return Array.Empty<CSharpParameterInfo>();
    }

    private static string ResolveAccessibility(SyntaxTokenList modifiers, string fallback)
    {
        var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var hasInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        if (hasPublic) return "public";
        if (hasProtected && hasInternal) return "protected internal";
        if (hasProtected && hasPrivate) return "private protected";
        if (hasInternal) return "internal";
        if (hasProtected) return "protected";
        if (hasPrivate) return "private";
        return fallback;
    }

    private static (string? BaseType, IReadOnlyList<string> Interfaces) ExtractInheritance(BaseTypeDeclarationSyntax node)
    {
        if (node.BaseList is null || node.BaseList.Types.Count == 0)
            return (null, Array.Empty<string>());

        if (node is InterfaceDeclarationSyntax)
        {
            return (null, node.BaseList.Types.Select(t => t.Type.ToString()).ToArray());
        }

        var baseType = node.BaseList.Types[0].Type.ToString();
        var interfaces = node.BaseList.Types.Count > 1
            ? node.BaseList.Types.Skip(1).Select(t => t.Type.ToString()).ToArray()
            : Array.Empty<string>();
        return (baseType, interfaces);
    }

    private DocumentSpan ToDocumentSpan(TextSpan span)
    {
        return _lineMap.GetSpan(span.Start, span.End);
    }

    private Guid CreateNodeId(string category, TextSpan span) => CSharpIdFactory.CreateNodeId(_documentId, category, span);
    private Guid CreateSpanId(string category, TextSpan span) => CSharpIdFactory.CreateSpanId(_documentId, category, span);

    private sealed record NamespaceContext(Guid NodeId, string? QualifiedName);
    private sealed record TypeContext(Guid NodeId, string QualifiedName);

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
