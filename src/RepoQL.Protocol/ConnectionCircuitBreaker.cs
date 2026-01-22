namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Track repeated connection failures and temporarily block auto-reconnect attempts.
/// Complexity: Maintains a rolling failure window and open interval for a simple circuit breaker.
/// </summary>
internal sealed class ConnectionCircuitBreaker(int threshold, TimeSpan window)
{
    private readonly Queue<DateTime> _failures = new();
    private DateTime? _openedAt;

    public bool IsOpen(DateTime now)
    {
        Trim(now);
        if (_openedAt is null)
            return false;

        if (now - _openedAt >= window)
        {
            _openedAt = null;
            _failures.Clear();
            return false;
        }

        return true;
    }

    public void RecordFailure(DateTime now)
    {
        Trim(now);
        _failures.Enqueue(now);
        if (_failures.Count >= threshold)
        {
            _openedAt = now;
        }
    }

    public void RecordSuccess(DateTime now)
    {
        Trim(now);
        _failures.Clear();
        _openedAt = null;
    }

    public int FailureCount
    {
        get
        {
            Trim(DateTime.UtcNow);
            return _failures.Count;
        }
    }

    public TimeSpan Window => window;

    private void Trim(DateTime now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > window) 
            _failures.Dequeue();

        if (_openedAt is null || !(now - _openedAt >= window)) 
            return;
        
        _openedAt = null;
        _failures.Clear();
    }
}
