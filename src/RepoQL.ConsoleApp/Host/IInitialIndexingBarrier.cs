namespace RepoQL.ConsoleApp.Host;

public interface IInitialIndexingBarrier
{
    Task InitialScanCompleted { get; }
}