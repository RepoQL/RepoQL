using System.Text.Json.Serialization;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Preserve the live dashboard endpoint in diagnostics storage for other processes.
/// Complexity: Simple serialization contract shared by the host and dashboard command.
/// </summary>
internal sealed record DashboardBindReport(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("startedAt")] string? StartedAt);
