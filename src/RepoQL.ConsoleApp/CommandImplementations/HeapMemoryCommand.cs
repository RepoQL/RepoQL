using System.Globalization;
using Microsoft.Diagnostics.Runtime;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Expose top managed heap types in the host as an expensive diagnostics command.
/// Complexity: Ensures the host is available, resolves its PID, attaches out-of-process, and walks the live GC heap.
/// </summary>
[CommandClass]
internal sealed class HeapMemoryCommand
{
    private const int DefaultTopTypeCount = 15;
    private const int OneMb = 1024 * 1024;
    private readonly IHeapMemoryCommandOperations _operations;

    public HeapMemoryCommand(RepoQlClientProvider clientProvider)
        : this(new DefaultHeapMemoryCommandOperations(clientProvider))
    {
    }

    internal HeapMemoryCommand(IHeapMemoryCommandOperations operations)
    {
        _operations = operations;
    }

    [Command("diagnostics.memory.heap", Description = "Show top managed heap types in the host (expensive)")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        try
        {
            await _operations.EnsureHostAvailableAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to connect to the host before heap inspection: {ex.Message}");
        }

        var hostProcessId = _operations.TryGetHostProcessId();
        if (hostProcessId is null or <= 0)
        {
            return CommandResult.Error(
                "Could not determine the host PID. Ensure the RepoQL host is running for this repository, then retry.");
        }

        ManagedHeapSnapshot snapshot;
        try
        {
            snapshot = _operations.CaptureManagedHeapSnapshot(hostProcessId.Value, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (PlatformNotSupportedException ex)
        {
            return CommandResult.Error($"Heap inspection is not supported on this platform: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to inspect host PID {hostProcessId.Value}: {ex.Message}");
        }

        var lines = new List<string>
        {
            "Managed Heap",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"Host PID:           {snapshot.ProcessId,10:N0}",
            $"Total objects:      {snapshot.TotalObjects,10:N0}",
            $"Total shallow size: {Mb(snapshot.TotalSizeBytes),10} MB"
        };

        if (snapshot.Types.Count == 0)
        {
            lines.Add(string.Empty);
            lines.Add("No managed objects were reported from the host heap.");
        }
        else
        {
            lines.Add("Top managed types by shallow size:");

            foreach (var type in snapshot.Types)
            {
                lines.Add(
                    $"  {Truncate(type.TypeName, 40),-40} {type.ObjectCount,10:N0} objs {Mb(type.TotalSizeBytes),8} MB   [{type.HeapKinds}]");
            }
        }

        lines.AddRange(
        [
            string.Empty,
            "Notes:",
            "  - shallow managed bytes only; retained size is not computed",
            "  - native allocations (DuckDB, ONNX, runtime) are excluded",
            "  - use ::diagnostics.memory for the full process memory breakdown"
        ]);

        return CommandResult.Success(string.Join(Environment.NewLine, lines));
    }

    private static string Mb(long bytes) =>
        (bytes / (double)OneMb).ToString("N0", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength
            ? value
            : value[..(maxLength - 3)] + "...";

    private static int Percent(long value, long total)
        => total > 0
            ? (int)Math.Round(100.0 * value / total, MidpointRounding.AwayFromZero)
            : 0;

    private static string FormatHeapKind(string kind)
    {
        if (kind.Contains("large", StringComparison.OrdinalIgnoreCase))
            return "LOH";

        if (kind.Contains("pinned", StringComparison.OrdinalIgnoreCase))
            return "POH";

        if (kind.Contains("frozen", StringComparison.OrdinalIgnoreCase))
            return "Frozen";

        if (kind.Contains("0", StringComparison.Ordinal))
            return "Gen0";

        if (kind.Contains("1", StringComparison.Ordinal))
            return "Gen1";

        if (kind.Contains("2", StringComparison.Ordinal))
            return "Gen2";

        return kind;
    }

    private sealed class DefaultHeapMemoryCommandOperations(RepoQlClientProvider clientProvider) : IHeapMemoryCommandOperations
    {
        public async ValueTask EnsureHostAvailableAsync(CancellationToken cancellationToken)
        {
            _ = await clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        }

        public int? TryGetHostProcessId()
        {
            var trackedPid = RepoQlClient.GetHostDiagnostics().ProcessId;
            if (trackedPid > 0)
                return trackedPid;

            try
            {
                var repoRoot = clientProvider.GetConfiguredRepositoryPath() ?? RepoLocator.FindRepoRoot();
                if (!string.IsNullOrWhiteSpace(repoRoot)
                    && HostLock.TryReadHolderPid(repoRoot, out var lockPid)
                    && lockPid > 0)
                {
                    return lockPid;
                }
            }
            catch
            {
                // Best effort fallback; caller gets a clean PID-not-found error if this fails.
            }

            return null;
        }

        public ManagedHeapSnapshot CaptureManagedHeapSnapshot(int processId, CancellationToken cancellationToken)
        {
            using var target = DataTarget.AttachToProcess(processId, suspend: true);

            var clr = target.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("No CLR runtime was found in the host process.");

            var runtime = clr.CreateRuntime();
            var heap = runtime.Heap;
            if (!heap.CanWalkHeap)
                throw new InvalidOperationException("The host heap is not walkable right now. Retry when the host is idle.");

            long totalBytes = 0;
            long totalObjects = 0;
            var types = new Dictionary<string, MutableManagedHeapTypeStat>(StringComparer.Ordinal);

            foreach (var segment in heap.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var heapKind = FormatHeapKind(segment.Kind.ToString());

                foreach (var obj in segment.EnumerateObjects())
                {
                    if ((totalObjects & 0x3FFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();

                    if (!obj.IsValid || obj.IsFree || obj.Type is null)
                        continue;

                    var size = obj.Size > long.MaxValue ? long.MaxValue : (long)obj.Size;
                    if (size <= 0)
                        continue;

                    totalObjects++;
                    totalBytes = SaturatingAdd(totalBytes, size);

                    var typeName = string.IsNullOrWhiteSpace(obj.Type.Name)
                        ? "<unknown>"
                        : obj.Type.Name!;

                    if (!types.TryGetValue(typeName, out var stat))
                    {
                        stat = new MutableManagedHeapTypeStat(typeName);
                        types.Add(typeName, stat);
                    }

                    stat.ObjectCount++;
                    stat.TotalSizeBytes = SaturatingAdd(stat.TotalSizeBytes, size);
                    stat.AddHeapBytes(heapKind, size);
                }
            }

            var topTypes = types.Values
                .OrderByDescending(t => t.TotalSizeBytes)
                .ThenByDescending(t => t.ObjectCount)
                .Take(DefaultTopTypeCount)
                .Select(t => t.ToSnapshot())
                .ToList();

            return new ManagedHeapSnapshot(processId, totalObjects, totalBytes, topTypes);
        }

        private static long SaturatingAdd(long left, long right)
            => left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    internal interface IHeapMemoryCommandOperations
    {
        ValueTask EnsureHostAvailableAsync(CancellationToken cancellationToken);
        int? TryGetHostProcessId();
        ManagedHeapSnapshot CaptureManagedHeapSnapshot(int processId, CancellationToken cancellationToken);
    }

    internal sealed record ManagedHeapSnapshot(int ProcessId, long TotalObjects, long TotalSizeBytes, IReadOnlyList<ManagedHeapTypeStat> Types);

    internal sealed record ManagedHeapTypeStat(string TypeName, long ObjectCount, long TotalSizeBytes, string HeapKinds);

    private sealed class MutableManagedHeapTypeStat(string typeName)
    {
        private readonly Dictionary<string, long> _heapBytes = new(StringComparer.Ordinal);

        public string TypeName { get; } = typeName;
        public long ObjectCount { get; set; }
        public long TotalSizeBytes { get; set; }

        public void AddHeapBytes(string heapKind, long bytes)
        {
            if (_heapBytes.TryGetValue(heapKind, out var existing))
                _heapBytes[heapKind] = existing > long.MaxValue - bytes ? long.MaxValue : existing + bytes;
            else
                _heapBytes[heapKind] = bytes;
        }

        public ManagedHeapTypeStat ToSnapshot()
        {
            var heapKinds = _heapBytes.Count == 0
                ? "unknown"
                : string.Join(", ",
                    _heapBytes
                        .OrderByDescending(kvp => kvp.Value)
                        .Take(2)
                        .Select(kvp =>
                        {
                            var percent = Percent(kvp.Value, TotalSizeBytes);
                            var percentText = percent == 0 && kvp.Value > 0 ? "<1%" : $"{percent}%";
                            return $"{kvp.Key} {percentText}";
                        }));

            return new ManagedHeapTypeStat(TypeName, ObjectCount, TotalSizeBytes, heapKinds);
        }
    }
}
