namespace RepoQL.Web.Services;

/// <summary>
/// Tracks navigation history for back traversal.
/// Maintains a stack of recent entries (max 10) for edge traversal and general navigation.
/// </summary>
internal sealed class NavigationState : INavigationState
{
    private const int MaxHistorySize = 10;

    private readonly object _gate = new();
    private readonly Stack<NavigationEntry> _history = new();
    private NavigationEntry _current = new("overview");

    public NavigationEntry Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool CanGoBack
    {
        get
        {
            lock (_gate)
            {
                return _history.Count > 0;
            }
        }
    }

    public event Action? OnChange;

    public void NavigateTo(string view, NavigationParams? @params = null)
    {
        lock (_gate)
        {
            // Push current to history
            _history.Push(_current);

            // Trim history if too large
            while (_history.Count > MaxHistorySize)
            {
                // Remove oldest entry (bottom of stack)
                var temp = new Stack<NavigationEntry>();
                while (_history.Count > 1)
                {
                    temp.Push(_history.Pop());
                }
                _history.Pop(); // Discard oldest
                while (temp.Count > 0)
                {
                    _history.Push(temp.Pop());
                }
            }

            _current = new NavigationEntry(view, @params);
        }

        OnChange?.Invoke();
    }

    public void GoBack()
    {
        lock (_gate)
        {
            if (_history.Count == 0)
                return;

            _current = _history.Pop();
        }

        OnChange?.Invoke();
    }

    /// <summary>
    /// Syncs state when user navigates via browser/NavLink without going through NavigateTo.
    /// </summary>
    public void SyncFromUrl(string view, NavigationParams? @params = null)
    {
        lock (_gate)
        {
            // Only update if different from current
            if (_current.View == view && _current.Params == @params)
                return;

            _current = new NavigationEntry(view, @params);
        }

        // Don't fire OnChange here - this is sync only, not navigation
    }
}
