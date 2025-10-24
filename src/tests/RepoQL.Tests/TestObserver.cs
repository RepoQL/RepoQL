using RepoQL.Core;

namespace RepoQL.Tests;

internal sealed class TestObserver(Action<Exception> onError, Action<IndexerEvent> onNext) : IObserver<IndexerEvent>
{
    public void OnCompleted() { }
    public void OnError(Exception error) => onError(error);
    public void OnNext(IndexerEvent value) => onNext(value);
}