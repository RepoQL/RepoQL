using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Render Graphviz DOT notation to SVG inside a WASM sandbox.
/// Complexity: Singleton Engine+Module compiled once from embedded graphviz.wasm.
/// Per-call Store for isolation. Exports: graphviz_render, graphviz_free, malloc, free.
/// </summary>
public sealed class GraphvizRenderer : IDisposable
{
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly ILogger<GraphvizRenderer> _logger;
    private bool _disposed;

    public GraphvizRenderer(ILogger<GraphvizRenderer>? logger = null)
    {
        _logger = logger ?? NullLogger<GraphvizRenderer>.Instance;
        _engine = new Engine(new Config());
        var wasmBytes = LoadEmbeddedModule();
        _module = Module.FromBytes(_engine, "graphviz", wasmBytes);
        _logger.LogInformation("Graphviz WASM renderer initialized ({size} bytes)", wasmBytes.Length);
    }

    public string Render(string dot, string engine = "dot", string format = "svg")
    {
        using var store = new Store(_engine);
        store.SetWasiConfiguration(new WasiConfiguration());

        var linker = new Linker(_engine);
        linker.DefineWasi();

        var instance = linker.Instantiate(store, _module);
        var memory = instance.GetMemory("memory")
            ?? throw new InvalidOperationException("graphviz.wasm does not export 'memory'");

        // Initialize the WASI reactor
        var initialize = instance.GetAction("_initialize");
        initialize?.Invoke();

        var wasmMalloc = instance.GetFunction<int, int>("malloc")
            ?? throw new InvalidOperationException("graphviz.wasm does not export 'malloc'");
        var wasmFree = instance.GetAction<int>("free")
            ?? throw new InvalidOperationException("graphviz.wasm does not export 'free'");
        var render = instance.GetFunction<int, int, int, int, int, int, long>("graphviz_render")
            ?? throw new InvalidOperationException("graphviz.wasm does not export 'graphviz_render'");
        var gvFree = instance.GetAction<int>("graphviz_free")
            ?? throw new InvalidOperationException("graphviz.wasm does not export 'graphviz_free'");

        // Write DOT source to WASM memory
        var dotBytes = Encoding.UTF8.GetBytes(dot);
        var dotPtr = wasmMalloc(dotBytes.Length + 1);
        dotBytes.AsSpan().CopyTo(memory.GetSpan(dotPtr, dotBytes.Length + 1));
        memory.GetSpan(dotPtr, dotBytes.Length + 1)[dotBytes.Length] = 0;

        // Write engine string
        var engineBytes = Encoding.UTF8.GetBytes(engine);
        var enginePtr = wasmMalloc(engineBytes.Length + 1);
        engineBytes.AsSpan().CopyTo(memory.GetSpan(enginePtr, engineBytes.Length + 1));
        memory.GetSpan(enginePtr, engineBytes.Length + 1)[engineBytes.Length] = 0;

        // Write format string
        var formatBytes = Encoding.UTF8.GetBytes(format);
        var formatPtr = wasmMalloc(formatBytes.Length + 1);
        formatBytes.AsSpan().CopyTo(memory.GetSpan(formatPtr, formatBytes.Length + 1));
        memory.GetSpan(formatPtr, formatBytes.Length + 1)[formatBytes.Length] = 0;

        // Call graphviz_render
        var packed = render(dotPtr, dotBytes.Length, enginePtr, engineBytes.Length, formatPtr, formatBytes.Length);

        // Free input strings
        wasmFree(dotPtr);
        wasmFree(enginePtr);
        wasmFree(formatPtr);

        // Unpack result
        var resultPtr = (int)((ulong)packed >> 32);
        var resultLen = (int)(packed & 0xFFFFFFFF);

        if (resultPtr == 0 || resultLen <= 0)
            throw new InvalidOperationException("Graphviz render failed — check DOT syntax");

        var result = Encoding.UTF8.GetString(memory.GetSpan(resultPtr, resultLen));

        // Free result via graphviz_free (uses gvFreeRenderData internally)
        gvFree(resultPtr);

        return result;
    }

    private static byte[] LoadEmbeddedModule()
    {
        const string resourceName = "graphviz.wasm";

        var stream = typeof(GraphvizRenderer).Assembly.GetManifestResourceStream(resourceName);

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

        // Fallback: file next to executable
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
