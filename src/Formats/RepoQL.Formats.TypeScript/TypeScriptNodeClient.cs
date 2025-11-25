using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoQL.Formats.TypeScript;

public sealed class TypeScriptNodeClient : IAsyncDisposable, IDisposable
{
    private readonly ILogger<TypeScriptNodeClient> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = true
    };

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public TypeScriptNodeClient(ILogger<TypeScriptNodeClient>? logger = null)
    {
        _logger = logger ?? NullLogger<TypeScriptNodeClient>.Instance;
        CheckNodeAvailability();
    }

    private void CheckNodeAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var version = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    _logger.LogInformation("Node.js detected: {Version}", version);
                    return;
                }
            }
        }
        catch
        {
            // Fall through to warning
        }

        _logger.LogWarning(
            "Node.js not found in PATH. TypeScript/JavaScript parsing will fail. " +
            "Install Node.js from https://nodejs.org/ and run 'npm install' in: {Path}",
            Path.Combine(AppContext.BaseDirectory, "Node"));
    }

    public async Task<TypeScriptParseResult> ParseAsync(string path, string mediaKind, string sourceText, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return DiagnosticOnly(path, $"Failed to start Node helper for TypeScript/JavaScript parsing. Ensure Node and the 'typescript' package are available.");
            }

            var request = new NodeRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                MediaKind = mediaKind,
                Text = sourceText
            };

            var payload = JsonSerializer.Serialize(request, SerializerOptions);
            await _stdin!.WriteLineAsync(payload).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Read with timeout to prevent hanging if Node crashes
            var readTask = _stdout!.ReadLineAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completedTask = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask)
            {
                return DiagnosticOnly(path, "Node helper timed out after 30 seconds. File may be too large or complex.");
            }

            var line = await readTask.ConfigureAwait(false);
            if (line is null)
            {
                return DiagnosticOnly(path, "Node helper terminated unexpectedly while parsing.");
            }

            NodeResponse? response = null;
            try
            {
                response = JsonSerializer.Deserialize<NodeResponse>(line, SerializerOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize TypeScript parser response.");
            }

            if (response?.Ok == true && response.Result is not null)
            {
                return response.Result;
            }

            var error = response?.Error ?? $"Unknown parser failure. Raw response: {line}";
            _logger.LogWarning("TypeScript parser returned error: {Message}", error);
            return DiagnosticOnly(path, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript parser failed.");
            return DiagnosticOnly(path, ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                try { _stdin?.WriteLine("{\"id\":\"shutdown\"}"); } catch { /* ignored */ }
                try { _process.Kill(entireProcessTree: true); } catch { /* ignored */ }
            }

            _stdin?.Dispose();
            _stdout?.Dispose();
            _process?.Dispose();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<bool> EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _stdin is not null && _stdout is not null)
        {
            return true;
        }

        await StopProcessAsync().ConfigureAwait(false);

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Node", "ts-parser.js");
        if (!File.Exists(scriptPath))
        {
            _logger.LogError("TypeScript parser script not found at {Path}", scriptPath);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{scriptPath}\" --stdio",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null || proc.HasExited)
            {
                _logger.LogError("Failed to start Node process for TypeScript parser.");
                return false;
            }

            var stdin = proc.StandardInput;
            var stdout = proc.StandardOutput;

            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await proc!.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogDebug("TypeScript parser stderr: {Err}", stderr.Trim());
                    }
                }
                catch
                {
                    // ignored
                }
            }, cancellationToken);

            // Only assign to fields after everything succeeds
            _process = proc;
            _stdin = stdin;
            _stdout = stdout;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Node process for TypeScript parser.");

            // Clean up leaked process if we started it but failed to complete setup
            if (proc is { HasExited: false })
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignored */ }
            }
            proc?.Dispose();

            return false;
        }
    }

    private async Task StopProcessAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* ignored */ }
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _stdin = null;
            _stdout = null;
        }
    }

    private static TypeScriptParseResult DiagnosticOnly(string path, string message)
        => new()
        {
            Path = path,
            ScriptKind = string.Empty,
            Diagnostics = [new TypeScriptDiagnostic { Message = message }]
        };

    private sealed class NodeRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("mediaKind")]
        public string MediaKind { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    private sealed class NodeResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("result")]
        public TypeScriptParseResult? Result { get; set; }
    }
}
