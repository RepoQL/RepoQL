using RepoQL.Contracts;
using RepoQL.Contracts.Diagnostics;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Applies queue-control actions (cancel, skip, retry) against the in-memory registry.
/// </summary>
public sealed class QueueCommandService(
    UriRegistry registry,
    RepositoryConfiguration repoConfig,
    IIndexingDiagnosticsProvider? diagnostics = null)
{
    private readonly UriRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly RepositoryConfiguration _repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
    private readonly IIndexingDiagnosticsProvider? _diagnostics = diagnostics;

    public QueueControlOutcome Execute(QueueControlAction action, string? uriInput)
    {
        if (!TryParseFileUri(uriInput, out var uri, out var error))
            return QueueControlOutcome.Error(error!);

        return action switch
        {
            QueueControlAction.Cancel => Cancel(uri!),
            QueueControlAction.Skip => Skip(uri!),
            QueueControlAction.Retry => Retry(uri!),
            _ => QueueControlOutcome.Error($"Unsupported queue action: {action}")
        };
    }

    private QueueControlOutcome Cancel(RepoUri uri)
    {
        if (!_registry.TryGetValue(uri, out var entry))
            return QueueControlOutcome.Error($"Not found: {uri}");

        if (entry.Status is UriStatus.Indexed or UriStatus.Failed or UriStatus.Skipped)
            return QueueControlOutcome.Ok($"Already {entry.Status}: {uri}");

        var previousStatus = entry.Status;
        var stage = ResolveStage(uri, previousStatus);
        _registry.SetFailed(uri, "Cancelled by user");
        return QueueControlOutcome.Ok($"Cancelled: {uri} (was {previousStatus} in {stage})");
    }

    private QueueControlOutcome Skip(RepoUri uri)
    {
        if (_registry.TryGetValue(uri, out var existing) && existing.Status == UriStatus.Skipped)
            return QueueControlOutcome.Ok($"Already skipped: {uri}");

        _registry.SetSkipped(uri, "Skipped by user");

        if (!SkipListFile.TryAppend(_repoConfig.Path, uri, out var error))
        {
            var path = SkipListFile.GetPath(_repoConfig.Path);
            return QueueControlOutcome.Error($"Failed to update skip list at {path}: {error}. In-memory status was updated.");
        }

        return QueueControlOutcome.Ok($"Skipped: {uri} (will not be processed)");
    }

    private QueueControlOutcome Retry(RepoUri uri)
    {
        if (!_registry.TryGetValue(uri, out var entry))
            return QueueControlOutcome.Error($"Not found: {uri}");

        if (entry.Status is not (UriStatus.Failed or UriStatus.Skipped))
            return QueueControlOutcome.Error($"Cannot retry: {uri} is {entry.Status}");

        var oldStatus = entry.Status;
        var oldError = entry.Error;
        _registry.SetDiscovered(uri);

        if (oldStatus == UriStatus.Skipped &&
            !SkipListFile.TryRemove(_repoConfig.Path, uri, out var error))
        {
            var path = SkipListFile.GetPath(_repoConfig.Path);
            return QueueControlOutcome.Error($"Failed to update skip list at {path}: {error}. In-memory status was updated.");
        }

        return QueueControlOutcome.Ok($"Re-enqueued: {uri} (previous: {oldStatus}, error: {oldError ?? "(none)"})");
    }

    private string ResolveStage(RepoUri uri, UriStatus status)
    {
        if (_diagnostics is not null)
        {
            var target = RepoUri.NormalizeContainer(uri);
            foreach (var queued in _diagnostics.GetQueuedItems())
            {
                if (TryNormalizeUri(queued.Uri, out var queuedUri) &&
                    string.Equals(queuedUri, target, StringComparison.OrdinalIgnoreCase))
                {
                    return queued.Stage;
                }
            }
        }

        return status switch
        {
            UriStatus.Discovered => "Discovery",
            UriStatus.Indexing => "HotPath",
            UriStatus.Stale => "Discovery",
            _ => "Unknown"
        };
    }

    private static bool TryNormalizeUri(string rawUri, out string normalized)
    {
        normalized = string.Empty;
        if (!RepoUri.TryParse(rawUri, out var parsed) || parsed is null)
            return false;

        normalized = RepoUri.NormalizeContainer(parsed);
        return true;
    }

    private static bool TryParseFileUri(string? uriInput, out RepoUri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(uriInput))
        {
            error = "URI required. Example: file:///src/App.cs or github://owner/repo/path";
            return false;
        }

        if (!RepoUri.TryParse(uriInput.Trim(), out var parsed) || parsed is null)
        {
            error = $"Invalid URI: {uriInput}. Example: file:///src/App.cs or github://owner/repo/path";
            return false;
        }

        uri = RepoUri.Parse(parsed.Container.AbsoluteUri);
        return true;
    }
}

public readonly record struct QueueControlOutcome(bool Success, string Message)
{
    public static QueueControlOutcome Ok(string message) => new(true, message);
    public static QueueControlOutcome Error(string message) => new(false, message);
}
