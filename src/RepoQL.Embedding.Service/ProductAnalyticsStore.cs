using Google.Cloud.Firestore;

namespace RepoQL.Embedding.Service;

/// <summary>
/// Purpose: Persist product analytics events (feedback, usage) to Firestore.
/// Complexity: Single collection write. Firestore handles ID generation and timestamps.
/// </summary>
internal sealed class ProductAnalyticsStore
{
    private readonly FirestoreDb? _db;
    private readonly ILogger<ProductAnalyticsStore> _logger;

    public ProductAnalyticsStore(IConfiguration config, ILogger<ProductAnalyticsStore> logger)
    {
        _logger = logger;

        var projectId = config["Firestore:ProjectId"];
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _logger.LogWarning("Firestore:ProjectId not configured — product analytics will be logged only");
            _db = null;
            return;
        }

        try
        {
            _db = FirestoreDb.Create(projectId);
            _logger.LogInformation("Product analytics store connected to Firestore project {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Firestore — product analytics will be logged only");
            _db = null;
        }
    }

    public async Task<bool> WriteFeedbackAsync(SubmitFeedbackRequest request, CancellationToken ct)
    {
        if (_db is null)
            return false;

        var doc = new Dictionary<string, object?>
        {
            ["type"] = "feedback",
            ["sessionId"] = request.SessionId,
            ["feedback"] = request.Feedback,
            ["diagnostics"] = request.Diagnostics,
            ["version"] = request.Version,
            ["platform"] = request.Platform,
            ["timestamp"] = FieldValue.ServerTimestamp
        };

        try
        {
            var docRef = await _db.Collection("product-analytics").AddAsync(doc, ct);
            _logger.LogInformation("Feedback stored in Firestore (doc={DocId}, session={SessionId})",
                docRef.Id, request.SessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write feedback to Firestore (session={SessionId})", request.SessionId);
            return false;
        }
    }
}
