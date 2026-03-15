namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Execute untrusted JavaScript inside a WASM sandbox with strict resource limits.
/// Complexity: Abstracts the WASM runtime (wasmtime) and JS engine (QuickJS-NG) behind
/// a simple evaluate interface. Per-call isolation via fresh Store with epoch/fuel/memory limits.
/// </summary>
public interface IWasmSandbox
{
    WasmExecutionResult Execute(
        string code,
        string? input = null,
        int timeoutMs = 5000,
        SandboxCapabilities? capabilities = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Purpose: Host-provided capabilities injected into the WASM sandbox at call time.
/// Complexity: Callbacks executed synchronously by host functions when JS calls repoql.query() etc.
/// </summary>
public sealed class SandboxCapabilities
{
    /// <summary>Execute SQL and return JSON array of row objects.</summary>
    public Func<string, string>? QueryHandler { get; init; }

    /// <summary>Read content at a URI and return a JSON result payload.</summary>
    public Func<string, int, string>? ReadHandler { get; init; }

    /// <summary>Write content to a URI. Return null on success, or an error message on failure.</summary>
    public Func<string, string, string?>? WriteHandler { get; init; }

    /// <summary>Delete content at a URI. Return null on success, or an error message on failure.</summary>
    public Func<string, string?>? DeleteHandler { get; init; }

    /// <summary>Load a module by specifier. Returns source code, or null if not found.</summary>
    public Func<string, string?>? ModuleLoaderHandler { get; init; }

    /// <summary>Execute an ffmpeg/ffprobe operation. Takes JSON args, returns JSON result.</summary>
    public Func<string, string>? FfmpegHandler { get; init; }

    /// <summary>Render DOT notation to SVG/JSON. Takes (dot, engine, format), returns rendered output.</summary>
    public Func<string, string, string, string>? GraphvizHandler { get; init; }

    /// <summary>Convert documents between formats via Pandoc. Takes JSON args, returns JSON result.</summary>
    public Func<string, string>? PandocHandler { get; init; }

    /// <summary>Rasterize SVG to PNG. Takes (svg, width), returns base64 PNG string.</summary>
    public Func<string, int, string>? SvgToPngHandler { get; init; }
}

public sealed record WasmExecutionResult
{
    public required bool Success { get; init; }
    public string? JsonOutput { get; init; }
    public string? ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorSuggestion { get; init; }
    public string? ErrorStack { get; init; }
    public required IReadOnlyList<WasmDiagnostic> Diagnostics { get; init; }
    public required long ElapsedMs { get; init; }
}

public sealed record WasmDiagnostic(string Level, string Message);
