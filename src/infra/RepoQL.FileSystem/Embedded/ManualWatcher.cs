namespace RepoQL.FileSystem.Embedded;

public sealed class ManualWatcher : FileSystemWatcherBase
{
    protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Raise(ResourceChange ev) => RaiseChange(ev);
}