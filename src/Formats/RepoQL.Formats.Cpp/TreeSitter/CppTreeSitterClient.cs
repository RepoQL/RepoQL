using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace RepoQL.Formats.Cpp.TreeSitter;

/// <summary>
/// Thread-safe Tree-sitter C/C++ parser client.
///
/// Purpose: Load the externally bundled tree-sitter-cpp grammar and parse source text.
///
/// Complexity: Grammar resolution by runtime identifier + per-thread parser lifecycle.
/// </summary>
public sealed class CppTreeSitterClient : IDisposable
{
    private const string GrammarLibraryBaseName = "tree-sitter-cpp";
    private const string GrammarEntryPoint = "tree_sitter_cpp";

    private readonly ILogger<CppTreeSitterClient> _logger;
    private readonly ThreadLocal<Parser?> _parsers;
    private readonly Language? _language;
    private readonly string? _loadFailure;
    private readonly string _runtimeBasePath;
    private bool _disposed;

    public CppTreeSitterClient(ILogger<CppTreeSitterClient>? logger = null, string? runtimeBasePath = null)
    {
        _logger = logger ?? NullLogger<CppTreeSitterClient>.Instance;
        _runtimeBasePath = string.IsNullOrWhiteSpace(runtimeBasePath)
            ? AppContext.BaseDirectory
            : runtimeBasePath!;

        (_language, _loadFailure) = CreateLanguage(_runtimeBasePath);
        _parsers = new ThreadLocal<Parser?>(() => _language is null ? null : new Parser(_language), trackAllValues: true);

        if (_language is null)
        {
            _logger.LogWarning(
                "C/C++ grammar unavailable. Parsing is disabled for this process. Reason: {Reason}",
                _loadFailure ?? "unknown load error");
        }
    }

    public bool IsGrammarAvailable => _language is not null;

    public CppParseResult Parse(string sourceCode)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (_language is null)
        {
            return CppParseResult.GrammarUnavailable(
                _loadFailure ?? "tree-sitter-cpp grammar is unavailable.");
        }

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

    private static (Language? Language, string? Error) CreateLanguage(string runtimeBasePath)
    {
        var rid = ResolveRuntimeIdentifier();
        if (rid is null)
        {
            return (null, $"unsupported runtime: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        }

        var nativeFileName = ResolveNativeLibraryFileName();
        if (nativeFileName is null)
        {
            return (null, $"unsupported runtime for native library name resolution: {rid}");
        }

        var grammarPath = Path.Combine(runtimeBasePath, "runtimes", rid, "native", nativeFileName);
        if (!File.Exists(grammarPath))
        {
            return (null, $"grammar native library was not found: {grammarPath}");
        }

        try
        {
            return (new Language(grammarPath, GrammarEntryPoint), null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (null, $"failed to load grammar native library ({grammarPath}): {ex.Message}");
        }
    }

    private static string? ResolveRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => null
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => null
            };
        }

        return null;
    }

    private static string? ResolveNativeLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{GrammarLibraryBaseName}.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"lib{GrammarLibraryBaseName}.so";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"lib{GrammarLibraryBaseName}.dylib";
        }

        return null;
    }

    private static int CountErrorNodes(Node root)
    {
        var count = (root.IsError || root.IsMissing) ? 1 : 0;
        foreach (var child in root.NamedChildren)
        {
            count += CountErrorNodes(child);
        }

        if (count == 0 && root.HasError)
        {
            return 1;
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

    public static CppParseResult GrammarUnavailable(string diagnostic)
        => new(tree: null, grammarAvailable: false, diagnostic: diagnostic, errorNodeCount: 0);

    public static CppParseResult ParseFailure(string diagnostic)
        => new(tree: null, grammarAvailable: true, diagnostic: diagnostic, errorNodeCount: 0);

    public void Dispose()
    {
        Tree?.Dispose();
    }
}
