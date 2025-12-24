using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal sealed class SymbolReferenceCollector : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly IReadOnlyDictionary<SyntaxNode, Guid> _declaredNodeIds;
    private readonly TextLineMap _lineMap;
    private readonly Guid _documentId;
    private readonly List<CSharpSymbolReference> _references = new();
    private readonly Dictionary<SyntaxNode, Guid?> _ownerCache = new(ReferenceEqualityComparer.Instance);

    public SymbolReferenceCollector(
        SemanticModel semanticModel,
        IReadOnlyDictionary<SyntaxNode, Guid> declaredNodeIds,
        TextLineMap lineMap,
        Guid documentId)
        : base(SyntaxWalkerDepth.Node)
    {
        _semanticModel = semanticModel;
        _declaredNodeIds = declaredNodeIds;
        _lineMap = lineMap;
        _documentId = documentId;
    }

    public IReadOnlyList<CSharpSymbolReference> References => _references;

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        RecordReference(node);
        base.VisitIdentifierName(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        RecordReference(node);
        base.VisitGenericName(node);
    }

    private void RecordReference(SyntaxNode node)
    {
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol
                     ?? _semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
            return;

        var key = CSharpSemanticUtilities.BuildSymbolKey(symbol);
        var targetNodeId = TryResolveTargetNodeId(symbol);
        if (targetNodeId is null)
            return;

        var sourceNodeId = ResolveOwner(node);
        if (sourceNodeId is null)
            return;

        var span = _lineMap.GetSpan(node.Span.Start, node.Span.End);
        _references.Add(new CSharpSymbolReference(sourceNodeId.Value, span, key, symbol.Kind.ToString(), targetNodeId));
    }

    private Guid? ResolveOwner(SyntaxNode node)
    {
        if (_ownerCache.TryGetValue(node, out var cached))
            return cached;

        var current = node;
        while (current is not null)
        {
            if (_declaredNodeIds.TryGetValue(current, out var nodeId))
            {
                _ownerCache[node] = nodeId;
                return nodeId;
            }
            current = current.Parent;
        }
        _ownerCache[node] = _documentId;
        return _documentId;
    }

    private Guid? TryResolveTargetNodeId(ISymbol symbol)
    {
        // Only resolve references to symbols declared in the current document
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxReference.GetSyntax();
            if (_declaredNodeIds.TryGetValue(syntax, out var nodeId))
                return nodeId;
        }
        return null;
    }
}
