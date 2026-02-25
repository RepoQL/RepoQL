using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace RepoQL.Formats.Cpp.TreeSitter;

/// <summary>
/// Thread-safe Tree-sitter C/C++ parser client.
///
/// Purpose: Load the tree-sitter-cpp grammar bundled by TreeSitter.DotNet and parse source text.
///
/// Complexity: Per-thread parser lifecycle with shared language instance.
/// </summary>
public sealed class CppTreeSitterClient : IDisposable
{
    private static readonly Language SharedLanguage = CreateLanguage();

    private readonly ILogger<CppTreeSitterClient> _logger;
    private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
    private bool _disposed;

    public CppTreeSitterClient(ILogger<CppTreeSitterClient>? logger = null)
    {
        _logger = logger ?? NullLogger<CppTreeSitterClient>.Instance;
    }

    public bool IsGrammarAvailable => true;

    public CppParseResult Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        try
        {
            var parser = _parsers.Value ?? throw new InvalidOperationException("Parser not initialized for current thread.");
            var tree = parser.Parse(sourceCode);
            if (tree is null)
            {
                return CppParseResult.ParseFailure("C/C++ parse returned no syntax tree.");
            }

            if (tree.RootNode.Id == IntPtr.Zero)
            {
                tree.Dispose();
                return CppParseResult.ParseFailure("C/C++ parse returned an empty root node.");
            }

            var errorNodeCount = CountErrorNodes(tree.RootNode);
            return CppParseResult.Success(tree, errorNodeCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "C/C++ parse failed.");
            return CppParseResult.ParseFailure($"C/C++ parse failed: {ex.Message}");
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
            parser?.Dispose();
        }

        _parsers.Dispose();
        _disposed = true;
    }

    private static Language CreateLanguage()
    {
        try
        {
            return new Language("tree-sitter-cpp", "tree_sitter_cpp");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to load tree-sitter C++ grammar from TreeSitter.DotNet (tree-sitter-cpp). Ensure package restore completed for the current RID.",
                ex);
        }
    }

    private static int CountErrorNodes(Node root)
    {
        if (!root.HasError && !root.IsError && !root.IsMissing)
        {
            return 0;
        }

        var count = (root.IsError || root.IsMissing) ? 1 : 0;
        foreach (var child in root.NamedChildren)
        {
            if (child.HasError || child.IsError || child.IsMissing)
            {
                count += CountErrorNodes(child);
            }
        }

        return count;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(CppTreeSitterClient));
    }
}

public sealed class CppParseResult : IDisposable
{
    private CppParseResult(Tree? tree, bool grammarAvailable, string? diagnostic, int errorNodeCount)
    {
        Tree = tree;
        GrammarAvailable = grammarAvailable;
        Diagnostic = diagnostic;
        ErrorNodeCount = errorNodeCount;
    }

    public Tree? Tree { get; }

    public bool GrammarAvailable { get; }

    public bool HasTree => Tree is not null;

    public string? Diagnostic { get; }

    public int ErrorNodeCount { get; }

    public Node? RootNode => Tree?.RootNode;

    public string? RootNodeType => RootNode?.Type;

    public static CppParseResult Success(Tree tree, int errorNodeCount)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new CppParseResult(tree, grammarAvailable: true, diagnostic: null, errorNodeCount);
    }

    public static CppParseResult ParseFailure(string diagnostic)
        => new(tree: null, grammarAvailable: true, diagnostic: diagnostic, errorNodeCount: 0);

    public void Dispose()
    {
        Tree?.Dispose();
    }
}
