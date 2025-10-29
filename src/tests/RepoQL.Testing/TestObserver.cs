using RepoQL.Core;

namespace RepoQL.Testing;

/// <summary>
/// Observer that forwards indexer events and errors to supplied delegates for test assertions.
/// </summary>
public sealed class TestObserver(Action<Exception> onError, Action<IndexerEvent> onNext) : IObserver<IndexerEvent>
{
    public void OnCompleted()
    {
    }

    public void OnError(Exception error) => onError(error);

    public void OnNext(IndexerEvent value) => onNext(value);
}
