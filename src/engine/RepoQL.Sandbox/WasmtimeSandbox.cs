using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Execute untrusted JavaScript inside a WASM sandbox via wasmtime + QuickJS-NG.
/// Complexity: Singleton Engine+Module compiled once. Per-call Store with epoch interruption
/// for timeout, memory limits, and host function callbacks for console.log capture.
/// String marshalling via exported alloc/dealloc and linear memory read/write.
/// </summary>
public sealed class WasmtimeSandbox : IWasmSandbox, IDisposable
{
    private const int DefaultTimeoutMs = 300_000;
    private const int EpochIntervalMs = 100;
    private const long MemoryLimitBytes = 128 * 1024 * 1024;

    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Timer _epochTimer;
    private readonly ILogger<WasmtimeSandbox> _logger;
    private bool _disposed;

    public WasmtimeSandbox(ILogger<WasmtimeSandbox>? logger = null)
    {
        _logger = logger ?? NullLogger<WasmtimeSandbox>.Instance;

        var config = new Config()
            .WithEpochInterruption(true);

        _engine = new Engine(config);

        var wasmBytes = LoadEmbeddedModule();
        _module = Module.FromBytes(_engine, "quickjs-evaluator", wasmBytes);

        // Tick the epoch every 100ms — stores set deadline based on timeout
        _epochTimer = new Timer(_ => _engine.IncrementEpoch(), null, EpochIntervalMs, EpochIntervalMs);

        _logger.LogInformation("WASM sandbox initialized ({size} bytes)", wasmBytes.Length);
    }

    public WasmExecutionResult Execute(
        string code,
        string? input = null,
        int timeoutMs = DefaultTimeoutMs,
        SandboxCapabilities? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(code))
        {
            return new WasmExecutionResult
            {
                Success = false,
                ErrorKind = "syntax",
                ErrorMessage = "Code cannot be empty",
                ErrorSuggestion = "Provide JavaScript source code to execute",
                Diagnostics = [],
                ElapsedMs = 0
            };
        }

        var sw = Stopwatch.StartNew();
        var diagnostics = new List<WasmDiagnostic>();

        try
        {
            return ExecuteCore(code, input, timeoutMs, capabilities, diagnostics, cancellationToken);
        }
        catch (WasmtimeException ex) when (ex.InnerException is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return ErrorResult("cancelled", "Execution was cancelled", "Retry the request", diagnostics, sw);
        }
        catch (WasmtimeException ex) when (IsEpochInterrupt(ex))
        {
            return ErrorResult("timeout", $"Script exceeded {timeoutMs}ms timeout", "Simplify the script or increase the timeout", diagnostics, sw);
        }
        catch (WasmtimeException ex) when (IsOutOfMemory(ex))
        {
            return ErrorResult("memory", "Script exceeded 16MB memory limit", "Reduce data size or process in smaller chunks", diagnostics, sw);
        }
        catch (WasmtimeException ex)
        {
            _logger.LogWarning(ex, "WASM execution failed");
            return ErrorResult("runtime", ex.Message, "Check the WASM module and host function bindings", diagnostics, sw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected sandbox error");
            return ErrorResult("runtime", ex.Message, "This may be a sandbox infrastructure error", diagnostics, sw);
        }
    }

    private WasmExecutionResult ExecuteCore(
        string code, string? input, int timeoutMs,
        SandboxCapabilities? capabilities,
        List<WasmDiagnostic> diagnostics, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        using var store = new Store(_engine);

        // Epoch deadline: timeoutMs / epochIntervalMs epochs
        var epochDeadline = Math.Max(1UL, (ulong)(timeoutMs / EpochIntervalMs));
        store.SetEpochDeadline(epochDeadline);

        // Memory limit
        store.SetLimits(memorySize: MemoryLimitBytes);

        // WASI (minimal — no filesystem, no args, no env)
        var linker = new Linker(_engine);
        linker.DefineWasi();
        store.SetWasiConfiguration(new WasiConfiguration());

        // Host function: repoql_log
        linker.DefineFunction("env", "repoql_log",
            (Caller caller, int level, int msgPtr, int msgLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || msgLen <= 0) return;

                var msg = Encoding.UTF8.GetString(memory.GetSpan(msgPtr, msgLen));
                var levelName = level switch
                {
                    1 => "warn",
                    2 => "error",
                    _ => "info"
                };
                diagnostics.Add(new WasmDiagnostic(levelName, msg));
            });

        // Host function: repoql_query — returns packed i64 (ptr << 32 | len)
        linker.DefineFunction("env", "repoql_query",
            (Caller caller, int sqlPtr, int sqlLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || sqlLen <= 0 || capabilities?.QueryHandler is null)
                    return 0L;

                var sql = Encoding.UTF8.GetString(memory.GetSpan(sqlPtr, sqlLen));

                string resultJson;
                try
                {
                    resultJson = capabilities.QueryHandler(sql);
                }
                catch (Exception ex)
                {
                    // Use string concat instead of anonymous types — trimmer strips anonymous type metadata
                    resultJson = "{\"__repoqlQueryError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultJson);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        linker.DefineFunction("env", "repoql_read",
            (Caller caller, int uriPtr, int uriLen, int budget) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || uriLen <= 0 || capabilities?.ReadHandler is null)
                    return 0L;

                var uri = Encoding.UTF8.GetString(memory.GetSpan(uriPtr, uriLen));

                string resultJson;
                try
                {
                    resultJson = capabilities.ReadHandler(uri, budget);
                }
                catch (Exception ex)
                {
                    resultJson = "{\"__repoqlReadError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultJson);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        linker.DefineFunction("env", "repoql_write",
            (Caller caller, int uriPtr, int uriLen, int contentPtr, int contentLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || uriLen <= 0 || capabilities?.WriteHandler is null)
                    return 0L;

                var uri = Encoding.UTF8.GetString(memory.GetSpan(uriPtr, uriLen));
                var content = contentLen > 0
                    ? Encoding.UTF8.GetString(memory.GetSpan(contentPtr, contentLen))
                    : string.Empty;

                string? error;
                try
                {
                    error = capabilities.WriteHandler(uri, content);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (error is null) return 0L;

                var errorBytes = Encoding.UTF8.GetBytes(error);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var errorPtr = (int)alloc.Invoke(errorBytes.Length + 1)!;
                if (errorPtr == 0) return 0L;

                var errorSpan = memory.GetSpan(errorPtr, errorBytes.Length + 1);
                errorBytes.AsSpan().CopyTo(errorSpan);
                errorSpan[errorBytes.Length] = 0;
                return ((long)(uint)errorPtr << 32) | (uint)errorBytes.Length;
            });

        linker.DefineFunction("env", "repoql_delete",
            (Caller caller, int uriPtr, int uriLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || uriLen <= 0 || capabilities?.DeleteHandler is null)
                    return 0L;

                var uri = Encoding.UTF8.GetString(memory.GetSpan(uriPtr, uriLen));

                string? error;
                try
                {
                    error = capabilities.DeleteHandler(uri);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (error is null) return 0L;

                var errorBytes = Encoding.UTF8.GetBytes(error);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var errorPtr = (int)alloc.Invoke(errorBytes.Length + 1)!;
                if (errorPtr == 0) return 0L;

                var errorSpan = memory.GetSpan(errorPtr, errorBytes.Length + 1);
                errorBytes.AsSpan().CopyTo(errorSpan);
                errorSpan[errorBytes.Length] = 0;
                return ((long)(uint)errorPtr << 32) | (uint)errorBytes.Length;
            });

        linker.DefineFunction("env", "repoql_load_module",
            (Caller caller, int specifierPtr, int specifierLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || specifierLen <= 0 || capabilities?.ModuleLoaderHandler is null)
                    return 0L;

                var specifier = Encoding.UTF8.GetString(memory.GetSpan(specifierPtr, specifierLen));

                string? source;
                try
                {
                    source = capabilities.ModuleLoaderHandler(specifier);
                }
                catch (Exception)
                {
                    return 0L; // module not found
                }

                if (source is null) return 0L;

                var sourceBytes = Encoding.UTF8.GetBytes(source);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var sourcePtr = (int)alloc.Invoke(sourceBytes.Length + 1)!;
                if (sourcePtr == 0) return 0L;

                var sourceSpan = memory.GetSpan(sourcePtr, sourceBytes.Length + 1);
                sourceBytes.AsSpan().CopyTo(sourceSpan);
                sourceSpan[sourceBytes.Length] = 0;
                return ((long)(uint)sourcePtr << 32) | (uint)sourceBytes.Length;
            });

        // Host function: repoql_ffmpeg — takes JSON args, returns JSON result
        linker.DefineFunction("env", "repoql_ffmpeg",
            (Caller caller, int jsonPtr, int jsonLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || jsonLen <= 0 || capabilities?.FfmpegHandler is null)
                    return 0L;

                var json = Encoding.UTF8.GetString(memory.GetSpan(jsonPtr, jsonLen));

                string resultJson;
                try
                {
                    resultJson = capabilities.FfmpegHandler(json);
                }
                catch (Exception ex)
                {
                    resultJson = "{\"__repoqlFfmpegError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultJson);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        // Host function: repoql_graphviz — takes DOT string + engine + format, returns SVG/rendered output
        linker.DefineFunction("env", "repoql_graphviz",
            (Caller caller, int dotPtr, int dotLen, int enginePtr, int engineLen, int fmtPtr, int fmtLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || dotLen <= 0 || capabilities?.GraphvizHandler is null)
                    return 0L;

                var dot = Encoding.UTF8.GetString(memory.GetSpan(dotPtr, dotLen));
                var engine = engineLen > 0 ? Encoding.UTF8.GetString(memory.GetSpan(enginePtr, engineLen)) : "dot";
                var format = fmtLen > 0 ? Encoding.UTF8.GetString(memory.GetSpan(fmtPtr, fmtLen)) : "svg";

                string resultStr;
                try
                {
                    resultStr = capabilities.GraphvizHandler(dot, engine, format);
                }
                catch (Exception ex)
                {
                    resultStr = "{\"__repoqlGraphvizError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultStr);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        // Host function: repoql_pandoc — takes JSON args, returns JSON result (same pattern as ffmpeg)
        linker.DefineFunction("env", "repoql_pandoc",
            (Caller caller, int jsonPtr, int jsonLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || jsonLen <= 0 || capabilities?.PandocHandler is null)
                    return 0L;

                var json = Encoding.UTF8.GetString(memory.GetSpan(jsonPtr, jsonLen));

                string resultJson;
                try
                {
                    resultJson = capabilities.PandocHandler(json);
                }
                catch (Exception ex)
                {
                    resultJson = "{\"__repoqlPandocError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultJson);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        // Host function: repoql_svg_to_png — takes SVG string + width, returns base64 PNG or error
        linker.DefineFunction("env", "repoql_svg_to_png",
            (Caller caller, int svgPtr, int svgLen, int width) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory is null || svgLen <= 0 || capabilities?.SvgToPngHandler is null)
                    return 0L;

                var svg = Encoding.UTF8.GetString(memory.GetSpan(svgPtr, svgLen));

                string resultStr;
                try
                {
                    resultStr = capabilities.SvgToPngHandler(svg, width);
                }
                catch (Exception ex)
                {
                    resultStr = "{\"__repoqlSvgError\":" + JsonSerializer.Serialize(ex.Message) + "}";
                }

                var resultBytes = Encoding.UTF8.GetBytes(resultStr);
                var alloc = caller.GetFunction("wasm_alloc");
                if (alloc is null) return 0L;

                var resultPtr = (int)alloc.Invoke(resultBytes.Length + 1)!;
                if (resultPtr == 0) return 0L;

                var resultSpan = memory.GetSpan(resultPtr, resultBytes.Length + 1);
                resultBytes.AsSpan().CopyTo(resultSpan);
                resultSpan[resultBytes.Length] = 0;
                return ((long)(uint)resultPtr << 32) | (uint)resultBytes.Length;
            });

        // Instantiate
        var instance = linker.Instantiate(store, _module);
        var memory = instance.GetMemory("memory")
            ?? throw new InvalidOperationException("WASM module does not export 'memory'");

        var wasmAlloc = instance.GetFunction<int, int>("wasm_alloc")
            ?? throw new InvalidOperationException("WASM module does not export 'wasm_alloc'");
        var wasmDealloc = instance.GetAction<int, int>("wasm_dealloc")
            ?? throw new InvalidOperationException("WASM module does not export 'wasm_dealloc'");
        var evaluate = instance.GetFunction<int, int, int, int, long>("evaluate")
            ?? throw new InvalidOperationException("WASM module does not export 'evaluate'");

        ct.ThrowIfCancellationRequested();

        // Write code into WASM memory
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var codePtr = wasmAlloc(codeBytes.Length);
        if (codePtr == 0)
            return ErrorResult("memory", "Failed to allocate WASM memory for code", "Code is too large", diagnostics, sw);
        codeBytes.AsSpan().CopyTo(memory.GetSpan(codePtr, codeBytes.Length));

        // Write input into WASM memory (if provided)
        var inputPtr = 0;
        var inputLen = 0;
        byte[]? inputBytes = null;
        if (!string.IsNullOrEmpty(input))
        {
            inputBytes = Encoding.UTF8.GetBytes(input);
            inputLen = inputBytes.Length;
            inputPtr = wasmAlloc(inputLen);
            if (inputPtr == 0)
            {
                wasmDealloc(codePtr, codeBytes.Length);
                return ErrorResult("memory", "Failed to allocate WASM memory for input", "Input is too large", diagnostics, sw);
            }
            inputBytes.AsSpan().CopyTo(memory.GetSpan(inputPtr, inputLen));
        }

        // Call evaluate
        var packed = evaluate(codePtr, codeBytes.Length, inputPtr, inputLen);

        // Unpack result
        var resultPtr = (int)((ulong)packed >> 32);
        var resultLen = (int)(packed & 0xFFFFFFFF);

        if (resultPtr == 0 || resultLen <= 0)
            return ErrorResult("runtime", "Evaluator returned no result", "Check your JavaScript code", diagnostics, sw);

        var resultJson = Encoding.UTF8.GetString(memory.GetSpan(resultPtr, resultLen));

        // Free WASM allocations
        wasmDealloc(codePtr, codeBytes.Length);
        if (inputPtr != 0) wasmDealloc(inputPtr, inputLen);
        wasmDealloc(resultPtr, resultLen);

        sw.Stop();

        // Check if the result is an error from the evaluator
        if (resultJson.StartsWith("{\"error\":", StringComparison.Ordinal))
        {
            var (kind, message, suggestion, stack) = ParseEvaluatorError(resultJson);
            return new WasmExecutionResult
            {
                Success = false,
                JsonOutput = resultJson,
                ErrorKind = kind,
                ErrorMessage = message,
                ErrorSuggestion = suggestion,
                ErrorStack = stack,
                Diagnostics = diagnostics,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        return new WasmExecutionResult
        {
            Success = true,
            JsonOutput = resultJson,
            Diagnostics = diagnostics,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private static (string kind, string message, string? suggestion, string? stack) ParseEvaluatorError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var err = doc.RootElement.GetProperty("error");
            var kind = err.TryGetProperty("kind", out var k) ? k.GetString() ?? "runtime" : "runtime";
            var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
            var suggestion = err.TryGetProperty("suggestion", out var s) ? s.GetString() : null;
            var stack = err.TryGetProperty("stack", out var st) ? st.GetString() : null;
            return (kind, message, suggestion, stack);
        }
        catch
        {
            return ("runtime", json, null, null);
        }
    }

    private static byte[] LoadEmbeddedModule()
    {
        const string resourceName = "quickjs-evaluator.wasm";

        // 1. Try embedded resource in this assembly
        var stream = typeof(WasmtimeSandbox).Assembly.GetManifestResourceStream(resourceName);

        // 2. Try all loaded assemblies (single-file publish merges assemblies)
        if (stream is null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { stream = asm.GetManifestResourceStream(resourceName); }
                catch { /* dynamic assemblies may throw */ }
                if (stream is not null) break;
            }
        }

        if (stream is not null)
        {
            using (stream)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // 3. Fallback: load from file next to the executable or in the repo
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, resourceName),
            Path.Combine(AppContext.BaseDirectory, "sandbox", "wasm", "dist", resourceName),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        throw new InvalidOperationException(
            $"WASM module '{resourceName}' not found. Searched: embedded resources, {string.Join(", ", candidates)}. " +
            "Build it with: /build-wasm-sandbox");
    }

    private static bool IsEpochInterrupt(WasmtimeException ex)
        => ex.Message.Contains("epoch", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("interrupt", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutOfMemory(WasmtimeException ex)
        => ex.Message.Contains("memory", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase);

    private static WasmExecutionResult ErrorResult(
        string kind, string message, string suggestion,
        IReadOnlyList<WasmDiagnostic> diagnostics, Stopwatch sw)
    {
        sw.Stop();
        return new WasmExecutionResult
        {
            Success = false,
            ErrorKind = kind,
            ErrorMessage = message,
            ErrorSuggestion = suggestion,
            Diagnostics = diagnostics,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _epochTimer.Dispose();
        _module.Dispose();
        _engine.Dispose();
    }
}
