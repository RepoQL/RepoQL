using System.Text;
using System.Linq;
using RepoQL.Contracts;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Capture degradable service startup outcomes for diagnostics.
/// Complexity: Aggregates service issues into a compact report.
/// </summary>
internal sealed class ServicesStartReport
{
    public List<ServiceStartIssue> Issues { get; } = [];

    public void AddIssue(ServiceDegradationKind kind, string message)
    {
        if (Issues.Any(issue => issue.Kind == kind))
            return;

        Issues.Add(new ServiceStartIssue(kind, message));
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Services:");

        if (Issues.Count == 0)
        {
            builder.AppendLine("  status: OK");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("  status: DEGRADED");
        foreach (var issue in Issues)
        {
            builder.AppendLine($"  - {issue.Kind.ToString().ToLowerInvariant()}: {issue.Message}");
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Purpose: Describe a degraded service and its primary issue.
/// Complexity: Simple data carrier for diagnostics.
/// </summary>
internal sealed record ServiceStartIssue(ServiceDegradationKind Kind, string Message);
