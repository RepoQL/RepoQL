using System.Collections.Generic;
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
    private readonly IReadOnlyDictionary<string, Guid>? _filePathToDocumentId;
    private readonly List<CSharpSymbolReference> _references = new();

    public SymbolReferenceCollector(
        SemanticModel semanticModel,
        IReadOnlyDictionary<SyntaxNode, Guid> declaredNodeIds,
        TextLineMap lineMap,
        Guid documentId,
        IReadOnlyDictionary<string, Guid>? filePathToDocumentId = null)
        : base(SyntaxWalkerDepth.StructuredTrivia)
    {
        _semanticModel = semanticModel;
        _declaredNodeIds = declaredNodeIds;
        _lineMap = lineMap;
        _documentId = documentId;
        _filePathToDocumentId = filePathToDocumentId;
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
        var current = node;
        while (current is not null)
        {
            if (_declaredNodeIds.TryGetValue(current, out var nodeId))
                return nodeId;
            current = current.Parent;
        }
        return _documentId;
    }

    private Guid? TryResolveTargetNodeId(ISymbol symbol)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxReference.GetSyntax();
            var tree = syntax.SyntaxTree;
            var path = tree?.FilePath;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            // Try to use the existing document ID from the mapping first
            Guid documentId;
            if (_filePathToDocumentId is not null)
            {
                var normalizedPath = System.IO.Path.GetFullPath(path);
                if (_filePathToDocumentId.TryGetValue(normalizedPath, out var mappedId))
                {
                    documentId = mappedId;
                }
                else
                {
                    // Fall back to computing from URI if not in mapping
                    var uri = CSharpLoader.GetRepoUriFromPath(path);
                    documentId = CSharpIdFactory.CreateDocumentId(uri);
                }
            }
            else
            {
                // No mapping available, compute from URI
                var uri = CSharpLoader.GetRepoUriFromPath(path);
                documentId = CSharpIdFactory.CreateDocumentId(uri);
            }

            var category = GetCategory(symbol);
            return CSharpIdFactory.CreateNodeId(documentId, category, syntax.Span);
        }
        return null;
    }

    private static string GetCategory(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol => "type",
        IMethodSymbol => "member",
        IPropertySymbol => "member",
        IFieldSymbol => "member",
        IEventSymbol => "member",
        _ => "member"
    };
}
