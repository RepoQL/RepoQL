using System.Text;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Manage host state files that must survive across MCP sessions.
/// Complexity: Encapsulates best-effort writes for host version and stderr ring-buffer mirroring.
/// </summary>
internal static class CrossSessionHostState
{
    // Path constants defined in RepoQL.Client.Diagnostics.HostPaths — use those for the canonical source.
    // Duplicated here because CrossSessionHostState has additional host-only behavior beyond path resolution.
    internal const int HostStderrRingBufferLineCount = 200;

    public static string GetHostStderrPath(string repoRoot)
        => RepoQL.Client.Diagnostics.HostPaths.GetHostStderrPath(repoRoot);

    public static string GetHostVersionPath(string repoRoot)
        => RepoQL.Client.Diagnostics.HostPaths.GetHostVersionPath(repoRoot);

    public static bool TryInstallStderrMirror(
        string repoRoot,
        out HostStderrMirrorScope? scope,
        out string stderrPath,
        out Exception? error)
    {
        scope = null;
        error = null;
        stderrPath = string.Empty;

        try
        {
            stderrPath = GetHostStderrPath(repoRoot);
            scope = new HostStderrMirrorScope(stderrPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            scope = null;
            return false;
        }
    }

    public static bool TryWriteHostVersionFile(
        string repoRoot,
        string version,
        out string versionPath,
        out Exception? error)
    {
        error = null;
        versionPath = string.Empty;
        try
        {
            versionPath = GetHostVersionPath(repoRoot);
            File.WriteAllText(versionPath, version, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}

/// <summary>
/// Purpose: Install and later restore Console.Error while mirroring stderr lines to disk.
/// Complexity: Manages process-global writer swap with deterministic restore on dispose.
/// </summary>
internal sealed class HostStderrMirrorScope : IDisposable
{
    private readonly TextWriter _originalErrorWriter;
    private readonly HostStderrFileMirror _mirrorWriter;
    private int _disposed;

    public HostStderrMirrorScope(string stderrPath)
    {
        _originalErrorWriter = Console.Error;
        _mirrorWriter = new HostStderrFileMirror(stderrPath, _originalErrorWriter);
        Console.SetError(_mirrorWriter);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Console.SetError(_originalErrorWriter);
        _mirrorWriter.Dispose();
    }
}

/// <summary>
/// Purpose: Mirror stderr writes to a bounded file-backed line buffer.
/// Complexity: Maintains newline-aware ring semantics while forwarding to an underlying writer.
/// </summary>
internal sealed class HostStderrFileMirror : TextWriter
{
    private readonly object _gate = new();
    private readonly TextWriter _passthrough;
    private readonly string _stderrPath;
    private readonly Queue<string> _lineBuffer;
    private readonly StringBuilder _pendingLine = new();
    private readonly int _maxLines;
    private bool _fileWriteDisabled;

    public HostStderrFileMirror(string stderrPath, TextWriter passthrough, int maxLines = CrossSessionHostState.HostStderrRingBufferLineCount)
    {
        if (string.IsNullOrWhiteSpace(stderrPath))
            throw new ArgumentException("stderr path is required.", nameof(stderrPath));
        if (passthrough is null)
            throw new ArgumentNullException(nameof(passthrough));
        if (maxLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLines), "max lines must be positive.");

        _stderrPath = stderrPath;
        _passthrough = passthrough;
        _maxLines = maxLines;
        _lineBuffer = new Queue<string>(maxLines);

        var directory = Path.GetDirectoryName(stderrPath)
                        ?? throw new InvalidOperationException($"Could not resolve directory for '{stderrPath}'.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(_stderrPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public override Encoding Encoding => _passthrough.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            _passthrough.Write(value);
            CaptureCharacterNoLock(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
            return;

        lock (_gate)
        {
            _passthrough.Write(value);
            CaptureTextNoLock(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _passthrough.WriteLine(value);
            if (value is not null)
                CaptureTextNoLock(value);
            CommitPendingLineNoLock();
        }
    }

    public override void WriteLine()
    {
        lock (_gate)
        {
            _passthrough.WriteLine();
            CommitPendingLineNoLock();
        }
    }

    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (_gate)
        {
            _passthrough.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_pendingLine.Length > 0)
                    CommitPendingLineNoLock();
                _passthrough.Flush();
            }
        }

        base.Dispose(disposing);
    }

    private void CaptureTextNoLock(string value)
    {
        foreach (var c in value)
        {
            CaptureCharacterNoLock(c);
        }
    }

    private void CaptureCharacterNoLock(char value)
    {
        if (value == '\n')
        {
            CommitPendingLineNoLock();
            return;
        }

        if (value != '\r')
            _pendingLine.Append(value);
    }

    private void CommitPendingLineNoLock()
    {
        var line = _pendingLine.ToString();
        _pendingLine.Clear();
        AppendLineNoLock(line);
    }

    private void AppendLineNoLock(string line)
    {
        if (_lineBuffer.Count >= _maxLines)
            _lineBuffer.Dequeue();
        _lineBuffer.Enqueue(line);
        PersistBufferNoLock();
    }

    private void PersistBufferNoLock()
    {
        if (_fileWriteDisabled)
            return;

        try
        {
            using var stream = new FileStream(_stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var line in _lineBuffer)
            {
                writer.WriteLine(line);
            }
        }
        catch
        {
            _fileWriteDisabled = true;
        }
    }
}
