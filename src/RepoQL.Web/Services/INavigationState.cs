namespace RepoQL.Web.Services;

/// <summary>
/// Manages view navigation with history for back traversal.
/// Enables edge traversal in Inspect view and general back navigation.
/// </summary>
public interface INavigationState
{
    /// <summary>
    /// Gets the current navigation entry.
    /// </summary>
    NavigationEntry Current { get; }

    /// <summary>
    /// Gets whether back navigation is possible.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Navigates to a new view, pushing current to history.
    /// </summary>
    void NavigateTo(string view, NavigationParams? @params = null);

    /// <summary>
    /// Navigates back to the previous entry in history.
    /// </summary>
    void GoBack();

    /// <summary>
    /// Fired when navigation state changes.
    /// </summary>
    event Action? OnChange;
}

/// <summary>
/// Represents a navigation history entry.
/// </summary>
/// <param name="View">The view/route name (e.g., "inspect", "query").</param>
/// <param name="Params">Optional navigation parameters.</param>
/// <param name="ScrollPosition">Scroll position to restore (future use).</param>
public sealed record NavigationEntry(
    string View,
    NavigationParams? Params = null,
    int? ScrollPosition = null);

/// <summary>
/// Parameters passed during navigation.
/// </summary>
/// <param name="Uri">Optional file/document URI for inspection.</param>
/// <param name="Line">Optional line number to scroll to.</param>
/// <param name="Query">Optional query or search text.</param>
public sealed record NavigationParams(
    string? Uri = null,
    int? Line = null,
    string? Query = null);
