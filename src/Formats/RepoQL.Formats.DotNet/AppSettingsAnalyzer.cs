using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Analyzer for .NET appsettings.json files.
/// Detects potential security issues and configuration problems.
/// </summary>
public sealed class AppSettingsAnalyzer : IFormatAnalyzer
{
    private static readonly SemanticMediaType AppSettingsType = SemanticMediaType
        .Create("application", "json")
        .WithKind("config.appsettings");

    /// <inheritdoc />
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return string.Equals(mediaType.Kind, AppSettingsType.Kind, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        DocumentModel document,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = document.GetMetadataOrDefault<AppSettingsState>(AppSettingsLoader.StateKey);
        if (state == null) yield break;

        // Rule 1: Potential secrets
        var secretRule = context.Settings.GetRule("config/potential-secret");
        if (secretRule.Severity != AnalysisSeverity.None)
        {
            foreach (var secret in state.PotentialSecrets)
            {
                yield return new AnalysisResult
                {
                    SemanticKey = $"{document.Uri}#rule:config/potential-secret@{secret.Path.GetHashCode():X8}",
                    RuleId = "config/potential-secret",
                    Source = "RepoQL.Config",
                    Kind = "lint",
                    Severity = secretRule.Severity,
                    Message = $"Potential hardcoded secret in '{secret.Path}'. Consider using user secrets, environment variables, or Azure Key Vault.",
                    Data = new JsonObject
                    {
                        ["path"] = secret.Path,
                        ["help"] = "https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets"
                    },
                    Target = new AnalysisTarget
                    {
                        TargetUri = document.Uri
                    }
                };
            }
        }

        // Rule 2: Production environment detection
        if (state.Environment?.Equals("Production", StringComparison.OrdinalIgnoreCase) == true)
        {
            var prodRule = context.Settings.GetRule("config/production-secrets");
            if (prodRule.Severity != AnalysisSeverity.None && state.PotentialSecrets.Count > 0)
            {
                yield return new AnalysisResult
                {
                    SemanticKey = $"{document.Uri}#rule:config/production-secrets",
                    RuleId = "config/production-secrets",
                    Source = "RepoQL.Config",
                    Kind = "lint",
                    Severity = AnalysisSeverity.Error,  // Always error for production
                    Message = $"Production configuration contains {state.PotentialSecrets.Count} potential hardcoded secret(s). Production configs should never contain secrets.",
                    Data = new JsonObject
                    {
                        ["secret_count"] = state.PotentialSecrets.Count,
                        ["help"] = "https://learn.microsoft.com/en-us/azure/key-vault/general/overview"
                    },
                    Target = new AnalysisTarget
                    {
                        TargetUri = document.Uri
                    }
                };
            }
        }

        // Rule 3: Missing connection strings in production
        if (state.Environment?.Equals("Production", StringComparison.OrdinalIgnoreCase) == true)
        {
            var connStrRule = context.Settings.GetRule("config/missing-connection-strings");
            if (connStrRule.Severity != AnalysisSeverity.None && state.ConnectionStringNames.Count == 0)
            {
                // Check if base appsettings.json has connection strings
                var baseUri = document.Uri.ToString().Replace(".Production.json", ".json", StringComparison.OrdinalIgnoreCase);
                if (!baseUri.Equals(document.Uri.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return new AnalysisResult
                    {
                        SemanticKey = $"{document.Uri}#rule:config/missing-connection-strings",
                        RuleId = "config/missing-connection-strings",
                        Source = "RepoQL.Config",
                        Kind = "lint",
                        Severity = connStrRule.Severity,
                        Message = "Production configuration is missing connection strings. Ensure production connection strings are configured.",
                        Data = new JsonObject
                        {
                            ["help"] = "Production configs should override development connection strings"
                        },
                        Target = new AnalysisTarget
                        {
                            TargetUri = document.Uri
                        }
                    };
                }
            }
        }

        await Task.CompletedTask;
    }
}
