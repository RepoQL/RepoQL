using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;
using RepoQL.Embedding;

namespace RepoQL.ConsoleApp.Feedback;

/// <summary>
/// Purpose: Send agent feedback to the RepoQL cloud service for product improvement.
/// Complexity: gRPC call to the embedding service's SubmitFeedback RPC.
/// Falls back to local JSONL if cloud is not configured or unreachable.
/// </summary>
internal sealed class FeedbackStore(ResolvedConfig config, ILogger<FeedbackStore> logger)
{
    private static readonly string Version =
        typeof(FeedbackStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FeedbackStore).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static readonly string Platform =
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public async Task SubmitAsync(string sessionId, string feedback, string diagnostics, CancellationToken ct)
    {
        var settings = config.Settings;
        var url = settings.Embedding.Remote.Url;
        var apiKey = settings.Cloud.ApiKey;

        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                await SubmitToCloudAsync(url, apiKey, sessionId, feedback, diagnostics, ct);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud feedback submission failed, falling back to local storage");
            }
        }

        await WriteLocalAsync(sessionId, feedback, diagnostics, ct);
    }

    private async Task SubmitToCloudAsync(
        string url, string apiKey,
        string sessionId, string feedback, string diagnostics,
        CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(url);
        var client = new EmbeddingService.EmbeddingServiceClient(channel);

        var headers = new Metadata { { "authorization", $"Bearer {apiKey}" } };

        var request = new SubmitFeedbackRequest
        {
            SessionId = sessionId,
            Feedback = feedback,
            Diagnostics = diagnostics,
            Version = Version,
            Platform = Platform
        };

        var response = await client.SubmitFeedbackAsync(request, headers, cancellationToken: ct);
        if (response.Accepted)
            logger.LogInformation("Feedback submitted to cloud (session={SessionId})", sessionId);
        else
            logger.LogWarning("Cloud rejected feedback (session={SessionId})", sessionId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private async Task WriteLocalAsync(string sessionId, string feedback, string diagnostics, CancellationToken ct)
    {
        var dir = config.UserConfigDir;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "feedback.jsonl");
        var entry = new { sessionId, feedback, diagnostics, version = Version, platform = Platform, timestamp = DateTimeOffset.UtcNow };
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
        logger.LogInformation("Feedback written locally to {Path} (session={SessionId})", path, sessionId);
    }
}
