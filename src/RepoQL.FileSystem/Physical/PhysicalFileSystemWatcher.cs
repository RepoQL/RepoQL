namespace RepoQL.FileSystem.Physical;

public sealed class PhysicalFileSystemWatcher(PhysicalFileSystem store) : FileSystemWatcherBase
{
    private FileSystemWatcher? _watcher;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _watcher = new FileSystemWatcher(store.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
        }
        return Task.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync()
    {
        if (_watcher == null)
            return ValueTask.CompletedTask;
        _watcher.Created -= OnCreated;
        _watcher.Changed -= OnChanged;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
        return ValueTask.CompletedTask;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        HandleChange(ResourceEvent.Created, e.FullPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        HandleChange(ResourceEvent.Updated, e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        HandleChange(ResourceEvent.Deleted, e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        HandleMove(e.OldFullPath, e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // If we can't create overflow event, raise the error
        RaiseError(e.GetException());
    }

    private void HandleChange(ResourceEvent kind, string absPath)
    {
        try
        {
            if (kind != ResourceEvent.Deleted && !File.Exists(absPath))
                return;
            var rel = Path.GetRelativePath(store.RootPath, absPath).Replace('\\', '/');
            if (string.Equals(rel, store.RootPath, StringComparison.OrdinalIgnoreCase))
                return;

            var uri = store.ToRepoUri(absPath);
            RaiseChange(new ResourceChange(kind, store.GetFile(uri), uri));
        }
        catch (Exception ex)
        {
            // Ignore to keep watcher alive
            RaiseError(ex);
        }
    }

    private void HandleMove(string oldAbs, string newAbs)
    {
        try
        {
            var oldRel = Path.GetRelativePath(store.RootPath, oldAbs).Replace('\\', '/');
            var newRel = Path.GetRelativePath(store.RootPath, newAbs).Replace('\\', '/');

            if (string.Equals(oldRel, store.RootPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(newRel, store.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var oldUri = store.ToRepoUri(oldAbs);
            var newUri = store.ToRepoUri(newAbs);
            RaiseChange(new ResourceChange(ResourceEvent.Moved, store.GetFile(newUri), newUri, oldUri));
        }
        catch (Exception ex)
        {
            // Ignore to keep watcher alive
            RaiseError(ex);
        }
    }
}