using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// MCP Proxy - lazy-spawns repoql mcp and proxies stdio.
/// Only starts child on first request or tool call to avoid locking database.
///
/// Special commands (sent as MCP tool calls):
/// - Tool "proxy_restart": Kills and restarts the repoql process
/// - Tool "proxy_status": Returns proxy and child process status
///
/// Usage: repoql-proxy [path-to-repoql-exe]
/// </summary>
class Program
{
    private static string? _repoqlPath;
    private static Process? _childProcess;
    private static readonly object _lock = new();
    private static volatile bool _stopping;
    private static int _restartCount;
    private static DateTime _lastStart = DateTime.MinValue;
    private static Task? _stdoutTask;
    private static Task? _stderrTask;

    static async Task<int> Main(string[] args)
    {
        _repoqlPath = FindRepoQlPath(args.Length > 0 ? args[0] : null);

        if (_repoqlPath == null)
        {
            await Console.Error.WriteLineAsync("[proxy] ERROR: Could not find repoql.exe");
            return 1;
        }

        await Console.Error.WriteLineAsync($"[proxy] Using: {_repoqlPath}");
        await Console.Error.WriteLineAsync("[proxy] Waiting for first request (lazy start)...");

        // Don't start child yet - wait for first request
        var stdinTask = ProxyStdinAsync();
        await stdinTask;

        _stopping = true;
        KillChild();

        return 0;
    }

    static string? FindRepoQlPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "repoql.exe"),
            Path.Combine(Environment.CurrentDirectory, "artifacts", "publish", "repoql.exe"),
            Path.Combine(Environment.CurrentDirectory, "repoql.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "repoql.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static bool EnsureChildRunning()
    {
        lock (_lock)
        {
            if (_childProcess != null && !_childProcess.HasExited)
                return true;

            return StartChildLocked();
        }
    }

    static bool StartChildLocked()
    {
        // Must be called under _lock
        try
        {
            // Kill any existing process
            if (_childProcess != null)
            {
                try
                {
                    if (!_childProcess.HasExited)
                    {
                        _childProcess.Kill(entireProcessTree: true);
                        _childProcess.WaitForExit(2000);
                    }
                }
                catch { }
                _childProcess = null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _repoqlPath,
                Arguments = "mcp",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            _childProcess = Process.Start(psi);
            if (_childProcess == null)
            {
                Console.Error.WriteLine("[proxy] Failed to start child process");
                return false;
            }

            _lastStart = DateTime.UtcNow;
            _restartCount++;

            // Start stdout/stderr forwarding tasks
            _stdoutTask = Task.Run(ForwardStdoutAsync);
            _stderrTask = Task.Run(ForwardStderrAsync);

            Console.Error.WriteLine($"[proxy] Started child PID={_childProcess.Id} (start #{_restartCount})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[proxy] Failed to start: {ex.Message}");
            return false;
        }
    }

    static void KillChild()
    {
        lock (_lock)
        {
            if (_childProcess != null)
            {
                try
                {
                    if (!_childProcess.HasExited)
                    {
                        Console.Error.WriteLine($"[proxy] Killing child PID={_childProcess.Id}");
                        _childProcess.Kill(entireProcessTree: true);
                        _childProcess.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[proxy] Kill error: {ex.Message}");
                }
                _childProcess = null;
            }
        }
    }

    static void RestartChild()
    {
        lock (_lock)
        {
            Console.Error.WriteLine("[proxy] Restarting child...");
            StartChildLocked();
        }
    }

    static async Task ProxyStdinAsync()
    {
        var buffer = new byte[65536];
        var stdin = Console.OpenStandardInput();

        try
        {
            while (!_stopping)
            {
                var bytesRead = await stdin.ReadAsync(buffer);
                if (bytesRead == 0)
                    break;

                var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Check for proxy control commands
                if (TryHandleProxyCommand(data, out var response))
                {
                    if (response != null)
                    {
                        await Console.Out.WriteAsync(response);
                        await Console.Out.FlushAsync();
                    }
                    continue;
                }

                // Ensure child is running before forwarding
                if (!EnsureChildRunning())
                {
                    await Console.Error.WriteLineAsync("[proxy] Failed to start child, dropping message");
                    continue;
                }

                // Forward to child (don't auto-restart on error - next request will start fresh)
                lock (_lock)
                {
                    if (_childProcess != null && !_childProcess.HasExited)
                    {
                        try
                        {
                            _childProcess.StandardInput.Write(data);
                            _childProcess.StandardInput.Flush();
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[proxy] Write error (child may have died): {ex.Message}");
                            // Don't restart - next EnsureChildRunning() call will handle it
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("[proxy] Child not running, message dropped. Will restart on next request.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[proxy] stdin error: {ex.Message}");
        }
    }

    static async Task ForwardStdoutAsync()
    {
        var buffer = new char[65536];
        Process? proc;

        lock (_lock)
        {
            proc = _childProcess;
        }

        if (proc == null)
            return;

        try
        {
            while (!_stopping && !proc.HasExited)
            {
                try
                {
                    var charsRead = await proc.StandardOutput.ReadAsync(buffer);
                    if (charsRead == 0)
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    await Console.Out.WriteAsync(buffer.AsMemory(0, charsRead));
                    await Console.Out.FlushAsync();
                }
                catch
                {
                    break;
                }
            }

            if (proc.HasExited)
            {
                await Console.Error.WriteLineAsync($"[proxy] Child exited (code={proc.ExitCode}). Will start fresh on next request.");
                // Clear the child reference so EnsureChildRunning knows to start fresh
                lock (_lock)
                {
                    if (_childProcess == proc)
                        _childProcess = null;
                }
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[proxy] stdout error: {ex.Message}");
        }
    }

    static async Task ForwardStderrAsync()
    {
        Process? proc;

        lock (_lock)
        {
            proc = _childProcess;
        }

        if (proc == null)
            return;

        try
        {
            while (!_stopping && !proc.HasExited)
            {
                try
                {
                    var line = await proc.StandardError.ReadLineAsync();
                    if (line != null)
                        await Console.Error.WriteLineAsync($"[child] {line}");
                }
                catch
                {
                    break;
                }
            }
        }
        catch { }
    }

    static bool TryHandleProxyCommand(string data, out string? response)
    {
        response = null;

        try
        {
            if (!data.Contains("proxy_restart", StringComparison.Ordinal) &&
                !data.Contains("proxy_status", StringComparison.Ordinal))
                return false;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("method", out var method))
                return false;

            if (method.GetString() != "tools/call")
                return false;

            if (!root.TryGetProperty("params", out var @params))
                return false;

            if (!@params.TryGetProperty("name", out var name))
                return false;

            var toolName = name.GetString();
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";

            if (toolName == "proxy_restart")
            {
                RestartChild();
                response = BuildToolResponse(id, $"Restarted repoql process (PID={_childProcess?.Id})");
                return true;
            }

            if (toolName == "proxy_status")
            {
                bool isRunning;
                int? pid;
                lock (_lock)
                {
                    isRunning = _childProcess != null && !_childProcess.HasExited;
                    pid = _childProcess?.Id;
                }

                var status = $"proxy_running: true\nchild_running: {isRunning}\nchild_pid: {pid}\nstart_count: {_restartCount}\nlast_start: {_lastStart:O}\nrepoql_path: {_repoqlPath}";
                response = BuildToolResponse(id, status);
                return true;
            }
        }
        catch
        {
            // Not valid JSON or not a proxy command
        }

        return false;
    }

    static string BuildToolResponse(string id, string content)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(id);
        sb.Append(",\"result\":{\"content\":[{\"type\":\"text\",\"text\":");
        sb.Append(JsonSerializer.Serialize(content));
        sb.Append("}]}}");
        sb.AppendLine();
        return sb.ToString();
    }
}
