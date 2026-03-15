using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Execute ffmpeg/ffprobe as a subprocess with structured input validation.
/// Complexity: Resolves ffmpeg binary, validates arguments, enforces scope on file URIs,
/// spawns process with captured output, enforces timeout.
/// </summary>
public sealed class FfmpegProcessRunner
{
    private const int MaxStderrBytes = 4096;

    private readonly string? _ffmpegPath;
    private readonly Func<string, string> _resolveFilePath;
    private readonly Action<string> _enforceRead;
    private readonly Action<string> _enforceWrite;
    private readonly int _timeoutMs;

    public FfmpegProcessRunner(
        string? ffmpegPath,
        Func<string, string> resolveFilePath,
        Action<string> enforceRead,
        Action<string> enforceWrite,
        int timeoutMs = 300_000)
    {
        _ffmpegPath = ffmpegPath;
        _resolveFilePath = resolveFilePath;
        _enforceRead = enforceRead;
        _enforceWrite = enforceWrite;
        _timeoutMs = timeoutMs;
    }

    public string Execute(string argsJson)
    {
        try
        {
            return ExecuteCore(argsJson);
        }
        catch (SandboxScopeException ex)
        {
            return ErrorJson(ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorJson(ex.Message);
        }
    }

    private string ExecuteCore(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // Extract required input
        if (!root.TryGetProperty("input", out var inputProp) || inputProp.ValueKind != JsonValueKind.String)
            return ErrorJson("'input' property is required and must be a file:// URI string.");

        var inputUri = inputProp.GetString()!;
        var isProbe = root.TryGetProperty("probe", out var probeProp) && probeProp.GetBoolean();

        // Enforce read scope and resolve input
        _enforceRead(inputUri);
        var inputPath = _resolveFilePath(inputUri);

        // Determine output (not needed for probe)
        string? outputPath = null;
        if (!isProbe)
        {
            if (root.TryGetProperty("output", out var outputProp) && outputProp.ValueKind == JsonValueKind.String)
            {
                var outputUri = outputProp.GetString()!;
                _enforceWrite(outputUri);
                outputPath = _resolveFilePath(outputUri);

                // Create output directory if needed
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDir))
                    Directory.CreateDirectory(outputDir);
            }
        }

        // Extract optional args
        var userArgs = new List<string>();
        if (root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsProp.EnumerateArray())
            {
                if (arg.ValueKind == JsonValueKind.String)
                    userArgs.Add(arg.GetString()!);
            }
        }

        // Extract optional activation bytes (for AAX)
        string? activationBytes = null;
        if (root.TryGetProperty("activationBytes", out var abProp) && abProp.ValueKind == JsonValueKind.String)
            activationBytes = abProp.GetString();

        // Find the binary
        var binary = FindBinary(isProbe ? "ffprobe" : "ffmpeg");
        if (binary is null)
        {
            var name = isProbe ? "ffprobe" : "ffmpeg";
            return ErrorJson(
                $"{name} not found in PATH. Install with 'winget install ffmpeg' or set sandbox.ffmpeg_path via ::config.set");
        }

        // Build arguments
        var processArgs = BuildArguments(isProbe, inputPath, outputPath, userArgs, activationBytes);

        // Spawn and capture
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = processArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {binary}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(_timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return ErrorJson($"ffmpeg exceeded timeout of {_timeoutMs}ms");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        sw.Stop();

        // Truncate stderr to avoid blowing up WASM memory
        if (stderr.Length > MaxStderrBytes)
            stderr = "...(truncated)...\n" + stderr[^MaxStderrBytes..];

        // Build result
        if (isProbe)
        {
            // ffprobe outputs JSON to stdout — parse and return directly
            try
            {
                var probeResult = JsonNode.Parse(stdout);
                if (probeResult is not null)
                    return probeResult.ToJsonString();
            }
            catch
            {
                // If stdout isn't valid JSON, wrap it
            }

            var fallback = new JsonObject
            {
                ["exitCode"] = process.ExitCode,
                ["stdout"] = stdout,
                ["stderr"] = stderr,
                ["duration"] = sw.ElapsedMilliseconds
            };
            return fallback.ToJsonString();
        }

        var result = new JsonObject
        {
            ["exitCode"] = process.ExitCode,
            ["stderr"] = stderr,
            ["duration"] = sw.ElapsedMilliseconds
        };
        return result.ToJsonString();
    }

    private static string BuildArguments(
        bool isProbe, string inputPath, string? outputPath,
        List<string> userArgs, string? activationBytes)
    {
        var args = new List<string>();

        if (isProbe)
        {
            args.AddRange(["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams"]);
            args.Add(Quote(inputPath));
        }
        else
        {
            if (activationBytes is not null)
                args.AddRange(["-activation_bytes", activationBytes]);

            args.AddRange(["-i", Quote(inputPath)]);
            args.AddRange(userArgs);

            if (outputPath is not null)
            {
                args.Add(Quote(outputPath));
                args.Add("-y"); // overwrite without asking
            }
        }

        return string.Join(' ', args);
    }

    private string? FindBinary(string name)
    {
        // Config path takes precedence
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            // If configured path points to ffmpeg but we need ffprobe, look in same directory
            if (name == "ffprobe" && _ffmpegPath.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                var probeCandidate = _ffmpegPath.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
                if (File.Exists(probeCandidate))
                    return probeCandidate;
            }
            if (File.Exists(_ffmpegPath) && (name == "ffmpeg" || _ffmpegPath.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return _ffmpegPath;
        }

        // Search PATH
        var exeName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"")}\"";

    private static string ErrorJson(string message)
        => "{\"__repoqlFfmpegError\":" + JsonSerializer.Serialize(message) + "}";
}
