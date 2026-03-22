using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Convert documents between formats using Pandoc compiled to WASM.
/// Complexity: Singleton Engine+Module compiled once from embedded pandoc.wasm (~52MB).
/// Per-call Store with WASI stdin/stdout/stderr for CLI-style invocation.
/// Unlike GraphvizRenderer (reactor mode), Pandoc uses _start() (command mode).
/// </summary>
public sealed class PandocRenderer : IDisposable
{
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly ILogger<PandocRenderer> _logger;
    private bool _disposed;

    public PandocRenderer(ILogger<PandocRenderer>? logger = null)
    {
        _logger = logger ?? NullLogger<PandocRenderer>.Instance;
        _engine = new Engine(new Config());

        _logger.LogInformation("Compiling pandoc.wasm (this takes a moment on first load)...");
        var wasmBytes = LoadEmbeddedModule();
        _module = Module.FromBytes(_engine, "pandoc", wasmBytes);
        _logger.LogInformation("Pandoc WASM renderer initialized ({size:N0} bytes)", wasmBytes.Length);
    }

    /// <summary>
    /// Convert document content between formats.
    /// </summary>
    /// <param name="input">The document content to convert.</param>
    /// <param name="from">Input format (e.g., "markdown", "html", "latex", "docx").</param>
    /// <param name="to">Output format (e.g., "html", "markdown", "latex", "plain").</param>
    /// <param name="extraArgs">Optional additional pandoc arguments.</param>
    /// <returns>The converted document content.</returns>
    public string Convert(string input, string from = "markdown", string to = "html", IReadOnlyList<string>? extraArgs = null)
    {
        // Build pandoc CLI arguments
        var args = new List<string> { "pandoc", "-f", from, "-t", to };
        if (extraArgs is not null)
            args.AddRange(extraArgs);

        // Write input to a temp file via WASI filesystem (pandoc reads from stdin)
        var inputBytes = Encoding.UTF8.GetBytes(input);

        // Create a WASI config with stdin from the input bytes
        // and stdout/stderr captured to files
        var stdinPath = Path.GetTempFileName();
        var stdoutPath = Path.GetTempFileName();
        var stderrPath = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(stdinPath, inputBytes);

            // Scope the Store so WASI releases file handles before we read output
            {
                using var store = new Store(_engine);
                var wasiConfig = new WasiConfiguration()
                    .WithArgs(args.ToArray())
                    .WithStandardInput(stdinPath)
                    .WithStandardOutput(stdoutPath)
                    .WithStandardError(stderrPath);

                store.SetWasiConfiguration(wasiConfig);

                var linker = new Linker(_engine);
                linker.DefineWasi();

                var instance = linker.Instantiate(store, _module);

                var start = instance.GetAction("_start");
                if (start is null)
                    throw new InvalidOperationException("pandoc.wasm does not export '_start'");

                try
                {
                    start();
                }
                catch (WasmtimeException ex) when (ex.Message.Contains("exit", StringComparison.OrdinalIgnoreCase))
                {
                    // Pandoc calls proc_exit(0) on success — wasmtime throws for this.
                    // This is normal. We check stderr below after the Store is disposed.
                }
            }
            // Store disposed — WASI file handles released, safe to read

            var output = File.ReadAllText(stdoutPath, Encoding.UTF8);
            var stderrOutput = File.ReadAllText(stderrPath, Encoding.UTF8).Trim();

            if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(stderrOutput))
                throw new InvalidOperationException($"Pandoc error: {stderrOutput}");

            return output;
        }
        finally
        {
            try { File.Delete(stdinPath); } catch { }
            try { File.Delete(stdoutPath); } catch { }
            try { File.Delete(stderrPath); } catch { }
        }
    }

    private static byte[] LoadEmbeddedModule()
    {
        const string resourceName = "pandoc.wasm";

        var stream = typeof(PandocRenderer).Assembly.GetManifestResourceStream(resourceName);

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
            $"WASM module '{resourceName}' not found. Searched: embedded resources, {string.Join(", ", candidates)}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _module.Dispose();
        _engine.Dispose();
    }
}
