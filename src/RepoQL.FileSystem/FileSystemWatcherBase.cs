using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem;

/// <summary>
///     Abstract base class that simplifies implementing <see cref="IFileSystemWatcher"/>.
///     Provides observable pattern implementation and lifecycle management.
/// </summary>
public abstract class FileSystemWatcherBase : IFileSystemWatcher
{
    private readonly List<IObserver<ResourceChange>> _observers = [];
    private readonly Lock _lock = new();
    private bool _isStarted;
    private bool _isDisposed;

    /// <summary>
    ///     Gets whether the watcher is currently started.
    /// </summary>
    protected bool IsStarted => _isStarted;

    /// <summary>
    ///     Gets whether the watcher has been disposed.
    /// </summary>
    protected bool IsDisposed => _isDisposed;

    /// <summary>
    ///     Subscribes an observer to receive change notifications.
    /// </summary>
    public virtual IDisposable Subscribe(IObserver<ResourceChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_lock)
        {
            if (_isDisposed)
            {
                observer.OnError(new ObjectDisposedException(GetType().Name));
                return new EmptyDisposable();
            }

            _observers.Add(observer);
            return new Unsubscriber(this, observer);
        }
    }

    /// <summary>
    ///     Starts the watcher asynchronously.
    /// </summary>
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isStarted)
                return;

            _isStarted = true;
        }

        await OnStartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stops the watcher asynchronously.
    /// </summary>
    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_isStarted)
                return;

            _isStarted = false;
        }

        await OnStopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disposes the watcher asynchronously.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        lock (_lock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
        }

        // Stop if still running
        if (_isStarted)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Complete all observers
        lock (_lock)
        {
            foreach (var observer in _observers)
            {
                try
                {
                    observer.OnCompleted();
                }
                catch
                {
                    // Swallow exceptions to ensure all observers are notified
                }
            }
            _observers.Clear();
        }

        await OnDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Raises a resource change event to all observers.
    /// </summary>
    /// <param name="change">The change to notify observers about.</param>
    protected void RaiseChange(ResourceChange change)
    {
        if (change == null)
            throw new ArgumentNullException(nameof(change));

        lock (_lock)
        {
            if (_isDisposed || !_isStarted)
                return;

            foreach (var observer in _observers.ToList())
            {
                try
                {
                    observer.OnNext(change);
                }
                catch
                {
                    // Swallow exceptions to keep watcher alive
                }
            }
        }
    }

    /// <summary>
    ///     Raises an error to all observers.
    /// </summary>
    /// <param name="error">The error to notify observers about.</param>
    protected void RaiseError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (_lock)
        {
            if (_isDisposed)
                return;

            foreach (var observer in _observers.ToList())
            {
                try
                {
                    observer.OnError(error);
                }
                catch
                {
                    // Swallow exceptions
                }
            }
        }
    }

    /// <summary>
    ///     Helper method to safely raise a change for a file path.
    /// </summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="file">The file which has changed</param>
    /// <param name="currentUri">The URI of the changed resource.</param>
    /// <param name="previousUri">For moves/renames, the previous URI.</param>
    protected void SafeRaiseChange(ResourceEvent kind, IFileInfo file, RepoUri currentUri, RepoUri? previousUri = null)
    {
        try
        {
            RaiseChange(new ResourceChange(kind, file, currentUri, previousUri));
        }
        catch
        {
            // Ignore to keep watcher alive
        }
    }

    /// <summary>
    ///     Called when the watcher is started. Override to implement start logic.
    /// </summary>
    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Called when the watcher is stopped. Override to implement stop logic.
    /// </summary>
    protected abstract Task OnStopAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Called when the watcher is disposed. Override to clean up resources.
    /// </summary>
    protected virtual ValueTask OnDisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void RemoveObserver(IObserver<ResourceChange> observer)
    {
        lock (_lock)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Unsubscriber(FileSystemWatcherBase watcher, IObserver<ResourceChange> observer)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            watcher.RemoveObserver(observer);
            _disposed = true;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}