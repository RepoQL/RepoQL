using System.Text;

namespace RepoQL.ConsoleApp.Diagnostics;

/// <summary>
/// Purpose: Capture socket binding context and failures for diagnostics.
/// Complexity: Tracks platform-specific path details with optional error information.
/// </summary>
internal sealed class SocketBindReport
{
    public required string SocketPath { get; init; }
    public int PathLength { get; set; }
    public string Platform { get; set; } = "unknown";
    public int PlatformLimit { get; set; }
    public bool SocketRedirected { get; set; }
    public string? MappingFilePath { get; set; }
    public bool BindSucceeded { get; set; }
    public string? BindError { get; set; }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Socket bind:");
        builder.AppendLine($"  socket: {SocketPath}");
        builder.AppendLine($"  path_length: {PathLength}");
        builder.AppendLine($"  platform: {Platform}");
        builder.AppendLine($"  platform_limit: {PlatformLimit}");
        builder.AppendLine($"  socket_redirected: {SocketRedirected}");
        if (!string.IsNullOrWhiteSpace(MappingFilePath))
            builder.AppendLine($"  mapping_file: {MappingFilePath}");
        builder.AppendLine($"  bind_succeeded: {BindSucceeded}");
        if (!string.IsNullOrWhiteSpace(BindError))
            builder.AppendLine($"  bind_error: {BindError}");
        return builder.ToString().TrimEnd();
    }
}
