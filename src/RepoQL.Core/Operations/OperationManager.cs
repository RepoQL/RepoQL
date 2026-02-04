using System.Collections.Concurrent;
using RepoQL.Contracts;

namespace RepoQL.Core.Operations;

/// <summary>
/// Creates and tracks operations for indexing work.
/// </summary>
/// <remarks>
/// <para><strong>Purpose</strong></para>
/// <para>
/// Provide a singleton registry for operation lifecycle tracking.
/// </para>
/// <para><strong>Complexity</strong></para>
/// <para>
/// Uses a thread-safe dictionary for storage while operations themselves manage polling and completion.
/// </para>
/// </remarks>
public sealed class OperationManager : IOperationManager
{
    private readonly UriRegistry _registry;
    private readonly ConcurrentDictionary<string, IOperation> _operations = new(StringComparer.OrdinalIgnoreCase);

    public OperationManager(UriRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IOperation CreateOperation(
        string description,
        IEnumerable<RepoUri> scope,
        IProgress<OperationProgress>? progress = null)
    {
        var operation = new Operation(_registry, description, scope, progress);
        _operations[operation.Id] = operation;
        return operation;
    }

    public IOperation? GetOperation(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return _operations.TryGetValue(id, out var operation) ? operation : null;
    }

    public IReadOnlyList<IOperation> Operations => _operations.Values.ToList();

    public IReadOnlyList<IOperation> ActiveOperations => _operations.Values
        .Where(operation => operation.State == OperationState.Running)
        .ToList();
}
