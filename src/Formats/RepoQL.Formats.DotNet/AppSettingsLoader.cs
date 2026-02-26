using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Lightweight loader + materializer for .NET appsettings.json files.
/// Extracts configuration context (sections, services, connection strings) without creating child nodes.
/// </summary>
public sealed class AppSettingsLoader(ITemplateRenderer? renderer = null) : IFormatLoader, IFormatMaterializer
{
    internal const string StateKey = "appsettings.state";

    private static readonly SemanticMediaType AppSettingsType = SemanticMediaType
        .Create("application", "json")
        .WithKind("config.appsettings");

    private readonly ITemplateRenderer _renderer = renderer ?? new LiquidTemplateRenderer(
        assembly: typeof(AppSettingsLoader).Assembly,
        resourceRoot: "RepoQL.Formats.DotNet.Templates",
        configure: StandardFilters.RegisterAll);

    /// <inheritdoc />
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (string.Equals(mediaType.Kind, AppSettingsType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <inheritdoc />
    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name;

        // Match appsettings*.json pattern
        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = AppSettingsType;
            return Task.FromResult(true);
        }

        // Also match launchSettings.json
        if (string.Equals(name, "launchsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = AppSettingsType;
            return Task.FromResult(true);
        }

        return Task.FromResult(artifact.MediaType is not null &&
            string.Equals(artifact.MediaType.Kind, "config.appsettings", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null) throw new InvalidOperationException("RepoUri required for appsettings loader.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = loaded.Text;
        var digest = loaded.Digest;

        // Extract environment from filename
        var environment = ExtractEnvironment(artifact.File.Name);

        try
        {
            using var jsonDoc = JsonDocument.Parse(text);

            // Extract context
            var topLevelKeys = new List<string>();
            var connectionStringNames = new List<string>();
            var detectedServices = new List<string>();
            var potentialSecrets = new List<PotentialSecret>();

            ExtractContext(jsonDoc.RootElement, topLevelKeys, connectionStringNames, detectedServices, potentialSecrets);

            var state = new AppSettingsState
            {
                Digest = digest,
                Size = loaded.ByteLength,
                MediaType = artifact.MediaType ?? AppSettingsType,
                StoreUri = artifact.RepoUri.ToString(),
                Environment = environment,
                TopLevelKeys = topLevelKeys,
                ConnectionStringNames = connectionStringNames,
                DetectedServices = detectedServices,
                PotentialSecrets = potentialSecrets
            };

            var metadata = new Dictionary<string, object?>
            {
                [StateKey] = state
            };

            return new DocumentModel(artifact.RepoUri, state.MediaType, text, metadata: metadata);
        }
        catch (JsonException)
        {
            // If parse fails, still create state but with no extracted data
            var emptyState = new AppSettingsState
            {
                Digest = digest,
                Size = loaded.ByteLength,
                MediaType = artifact.MediaType ?? AppSettingsType,
                StoreUri = artifact.RepoUri.ToString(),
                Environment = environment
            };

            return new DocumentModel(artifact.RepoUri, emptyState.MediaType, text,
                metadata: new Dictionary<string, object?> { [StateKey] = emptyState });
        }
    }

    /// <inheritdoc />
    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<AppSettingsState>(StateKey)
                    ?? throw new InvalidOperationException("appsettings missing state");

        // Build template model
        var fileName = GetFileName(document.Uri);
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["media_kind"] = state.MediaType.Kind ?? "config.appsettings",
            ["size_bytes"] = state.Size,
            ["line_count"] = document.LineMap.LineCount,
            ["token_count"] = tokenCount,
            ["environment"] = state.Environment,
            ["top_keys"] = state.TopLevelKeys,
            ["connection_strings"] = state.ConnectionStringNames,
            ["services"] = state.DetectedServices
        };

        var headline = _renderer.RenderAsync("explore/headline-appsettings", model).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("explore/summary-appsettings", model).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("explore/structure-appsettings", model).GetAwaiter().GetResult();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = document.Uri.ToString(),
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["environment"] = state.Environment,
                ["top_level_keys"] = new JsonArray(state.TopLevelKeys.Select(k => JsonValue.Create(k)).ToArray()),
                ["connection_strings"] = new JsonArray(state.ConnectionStringNames.Select(k => JsonValue.Create(k)).ToArray()),
                ["services"] = new JsonArray(state.DetectedServices.Select(s => JsonValue.Create(s)).ToArray())
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [docNode],
            Spans = [],
            Edges = []
        };
    }

    private static void ExtractContext(
        JsonElement root,
        List<string> topLevelKeys,
        List<string> connectionStringNames,
        List<string> detectedServices,
        List<PotentialSecret> potentialSecrets)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        // Extract top-level keys and special sections
        foreach (var prop in root.EnumerateObject())
        {
            topLevelKeys.Add(prop.Name);

            // Extract connection string names
            if (prop.Name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var cs in prop.Value.EnumerateObject())
                    {
                        connectionStringNames.Add(cs.Name);

                        // Detect service from connection string value
                        if (cs.Value.ValueKind == JsonValueKind.String)
                        {
                            var connStr = cs.Value.GetString() ?? "";
                            DetectServices(connStr, detectedServices);
                        }
                    }
                }
            }

            // Detect ApplicationInsights
            if (prop.Name.Equals("ApplicationInsights", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("AppInsights", detectedServices);
            }

            // Detect Authentication providers
            if (prop.Name.Equals("Authentication", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("Authentication", detectedServices);
            }
            else if (prop.Name.Equals("AzureAd", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("AzureAd", detectedServices);
            }
            else if (prop.Name.Equals("AzureAdB2C", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("AzureAdB2C", detectedServices);
            }
            else if (prop.Name.Equals("IdentityServer", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("IdentityServer", detectedServices);
            }

            // Detect other common sections
            if (prop.Name.Equals("HealthChecks", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("HealthChecks", detectedServices);
            }
            else if (prop.Name.Equals("Swagger", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("Swashbuckle", StringComparison.OrdinalIgnoreCase))
            {
                AddServiceIfNotPresent("Swagger", detectedServices);
            }
        }

        // Scan for potential secrets (recursive)
        ScanForSecrets(root, "", potentialSecrets);
    }

    private static void DetectServices(string connectionString, List<string> services)
    {
        if ((ContainsOrdinalIgnoreCase(connectionString, "data source") || ContainsOrdinalIgnoreCase(connectionString, "server="))
            && (ContainsOrdinalIgnoreCase(connectionString, "database=") || ContainsOrdinalIgnoreCase(connectionString, "initial catalog")))
        {
            AddServiceIfNotPresent("SqlServer", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "redis") || ContainsOrdinalIgnoreCase(connectionString, ":6379"))
        {
            AddServiceIfNotPresent("Redis", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "mongodb://") || ContainsOrdinalIgnoreCase(connectionString, ":27017"))
        {
            AddServiceIfNotPresent("MongoDB", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "cosmos") || ContainsOrdinalIgnoreCase(connectionString, ".documents.azure.com"))
        {
            AddServiceIfNotPresent("CosmosDB", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "rabbitmq://") || ContainsOrdinalIgnoreCase(connectionString, ":5672"))
        {
            AddServiceIfNotPresent("RabbitMQ", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "azureservicebus")
            || ContainsOrdinalIgnoreCase(connectionString, "servicebus.windows.net"))
        {
            AddServiceIfNotPresent("ServiceBus", services);
        }

        if (ContainsOrdinalIgnoreCase(connectionString, "accountname=")
            && ContainsOrdinalIgnoreCase(connectionString, "accountkey="))
        {
            AddServiceIfNotPresent("AzureStorage", services);
        }
    }

    private static void ScanForSecrets(JsonElement element, string path, List<PotentialSecret> secrets)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var currentPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}:{prop.Name}";

                    // Check if key name suggests secret
                    if (IsSecretKeyName(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString() ?? "";
                        // Don't flag if it's clearly a placeholder or environment variable
                        if (!IsPlaceholder(value))
                        {
                            // Approximate line number (would need more work for exact)
                            secrets.Add(new PotentialSecret(currentPath, 0));
                        }
                    }

                    ScanForSecrets(prop.Value, currentPath, secrets);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ScanForSecrets(item, $"{path}[{index}]", secrets);
                    index++;
                }
                break;
        }
    }

    private static bool IsSecretKeyName(string key)
    {
        return ContainsOrdinalIgnoreCase(key, "password")
               || ContainsOrdinalIgnoreCase(key, "secret")
               || ContainsOrdinalIgnoreCase(key, "apikey")
               || ContainsOrdinalIgnoreCase(key, "api_key")
               || ContainsOrdinalIgnoreCase(key, "token")
               || ContainsOrdinalIgnoreCase(key, "privatekey")
               || ContainsOrdinalIgnoreCase(key, "clientsecret")
               || (ContainsOrdinalIgnoreCase(key, "key")
                   && !ContainsOrdinalIgnoreCase(key, "publickey")
                   && !ContainsOrdinalIgnoreCase(key, "keyboard"));
    }

    private static bool IsPlaceholder(string value)
    {
        // Common placeholders that shouldn't be flagged
        return string.IsNullOrWhiteSpace(value) ||
               value.StartsWith('$') ||
               value.StartsWith("${", StringComparison.Ordinal) ||
               value.StartsWith('%') ||
               value.Equals("***", StringComparison.Ordinal) ||
               value.Equals("placeholder", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("your-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("<add-", StringComparison.OrdinalIgnoreCase) ||
               (value.StartsWith('<') && value.EndsWith('>'));
    }

    private static void AddServiceIfNotPresent(string service, List<string> services)
    {
        if (!services.Contains(service, StringComparer.OrdinalIgnoreCase))
            services.Add(service);
    }

    private static string? ExtractEnvironment(string fileName)
    {
        // appsettings.Development.json -> "Development"
        // appsettings.Production.json -> "Production"
        // appsettings.json -> null

        var match = Regex.Match(fileName, @"appsettings\.([^.]+)\.json", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp)) return Path.GetFileName(lp);
            }
        }
        catch { }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private static bool ContainsOrdinalIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
