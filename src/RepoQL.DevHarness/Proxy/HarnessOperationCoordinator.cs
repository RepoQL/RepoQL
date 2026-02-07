using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RepoQL.Contracts;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Coordinate harness lifecycle operations across sessions via a file lock.
/// Complexity: File-based locking with stale cleanup. Good error messages over ceremony.
/// </summary>
internal interface IHarnessOperationCoordinator
{
    /// <summary>Returns a human-readable description of the current lock, or null if idle.</summary>
    string? GetActiveLockDescription();

    /// <summary>
    /// Acquire the operation lock. Returns an IDisposable handle on success.
    /// Returns a null handle and a human-readable error if someone else holds the lock.
    /// </summary>
    (IDisposable? Handle, string? Error) TryAcquire(string sessionId, string operation, DateTimeOffset startedAt);

    /// <summary>Poll until the lock is released or timeout. Returns true if released.</summary>
    Task<bool> WaitForReleaseAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken);
}

internal sealed class HarnessOperationCoordinator : IHarnessOperationCoordinator
{
    internal const string LockFileName = "harness-operation.lock";
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;
    private readonly string? _lockPath;
    private int _initWarningLogged;

    public HarnessOperationCoordinator(string? repoRoot = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (message => Console.Error.WriteLine($"[HARNESS] {message}"));

        try
        {
            var root = string.IsNullOrWhiteSpace(repoRoot)
                ? RepoLocator.FindRepoRoot()
                : Path.GetFullPath(repoRoot);
            var repoqlDir = RepoLocator.EnsureRepoqlDirectory(root);
            _lockPath = Path.Combine(repoqlDir, LockFileName);
        }
        catch (Exception ex)
        {
            _lockPath = null;
            LogWarningOnce($"Failed to initialize harness coordination lock: {ex.Message}");
        }
    }

    public string? GetActiveLockDescription()
    {
        if (_lockPath is null || !File.Exists(_lockPath))
            return null;

        if (!TryReadActiveLock(_lockPath, out var info))
            return null;

        var elapsed = _clock() - info.StartedAt;
        return $"Session {info.SessionId} is {info.Operation} (started {elapsed.TotalSeconds:F0}s ago).";
    }

    public (IDisposable? Handle, string? Error) TryAcquire(string sessionId, string operation, DateTimeOffset startedAt)
    {
        if (_lockPath is null)
        {
            LogWarningOnce("Lock path unavailable. Proceeding without coordination.");
            return (new NoOpHandle(), null);
        }

        // Check for active lock first.
        if (TryReadActiveLock(_lockPath, out var existing))
        {
            var elapsed = _clock() - existing.StartedAt;
            var error = $"Session {existing.SessionId} is {existing.Operation} (started {elapsed.TotalSeconds:F0}s ago). " +
                        $"Use harness.wait_for_operation() to wait, or harness.status() to check progress.";
            return (null, error);
        }

        // Try to create the lock atomically.
        if (!TryCreateLock(_lockPath, sessionId, operation, startedAt))
        {
            // Race: someone else created it between our check and create.
            if (TryReadActiveLock(_lockPath, out var raceWinner))
            {
                var elapsed = _clock() - raceWinner.StartedAt;
                return (null, $"Session {raceWinner.SessionId} is {raceWinner.Operation} (started {elapsed.TotalSeconds:F0}s ago).");
            }

            return (new NoOpHandle(), null); // Lock file issue but no active lock - proceed.
        }

        return (new LockHandle(this, _lockPath, sessionId, operation), null);
    }

    public async Task<bool> WaitForReleaseAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (_lockPath is null || !File.Exists(_lockPath))
            return true;

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);

            if (!TryReadActiveLock(_lockPath, out _))
                return true;
        }

        return false;
    }

    internal void Release(string lockPath, string sessionId, string operation)
    {
        try
        {
            if (!File.Exists(lockPath))
                return;

            var content = File.ReadAllText(lockPath);
            if (TryParseLock(content, out var info) &&
                string.Equals(info.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(info.Operation, operation, StringComparison.Ordinal))
            {
                File.Delete(lockPath);
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to release harness lock: {ex.Message}");
        }
    }

    private bool TryReadActiveLock(string lockPath, out LockInfo info)
    {
        info = default!;
        if (!File.Exists(lockPath))
            return false;

        string content;
        try
        {
            content = File.ReadAllText(lockPath);
        }
        catch
        {
            return false;
        }

        if (!TryParseLock(content, out info))
        {
            // Unreadable lock - try to clean up by file age.
            TryCleanStaleByAge(lockPath);
            return false;
        }

        if (IsStale(info.StartedAt, _clock()))
        {
            TryDeleteLock(lockPath);
            return false;
        }

        return true;
    }

    private bool TryCreateLock(string lockPath, string sessionId, string operation, DateTimeOffset startedAt)
    {
        var payload = JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            operation,
            started_at = HarnessTimestampFormatter.Format(startedAt)
        });

        try
        {
            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(payload);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log($"Unable to create harness lock file: {ex.Message}");
            return false;
        }
    }

    private void TryDeleteLock(string lockPath)
    {
        try { File.Delete(lockPath); }
        catch (Exception ex) { _log($"Failed to delete harness lock file: {ex.Message}"); }
    }

    private void TryCleanStaleByAge(string lockPath)
    {
        try
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(lockPath), TimeSpan.Zero);
            if (IsStale(lastWrite, _clock()))
                TryDeleteLock(lockPath);
        }
        catch { /* best effort */ }
    }

    private void LogWarningOnce(string message)
    {
        if (Interlocked.Exchange(ref _initWarningLogged, 1) == 0)
            _log(message);
    }

    private static bool TryParseLock(string json, out LockInfo info)
    {
        info = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("session_id", out var s) || s.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("operation", out var o) || o.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("started_at", out var t) || t.ValueKind != JsonValueKind.String)
                return false;

            var sessionId = s.GetString();
            var operation = o.GetString();
            var startedAtRaw = t.GetString();

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(startedAtRaw))
                return false;

            if (!DateTimeOffset.TryParseExact(startedAtRaw, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startedAt))
                return false;

            info = new LockInfo(sessionId, operation, startedAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStale(DateTimeOffset startedAt, DateTimeOffset now) => now - startedAt > StaleThreshold;

    private sealed record LockInfo(string SessionId, string Operation, DateTimeOffset StartedAt);

    private sealed class LockHandle(HarnessOperationCoordinator coordinator, string lockPath, string sessionId, string operation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                coordinator.Release(lockPath, sessionId, operation);
        }
    }

    private sealed class NoOpHandle : IDisposable
    {
        public void Dispose() { }
    }
}
