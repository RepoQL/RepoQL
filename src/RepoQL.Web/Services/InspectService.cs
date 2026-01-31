namespace RepoQL.Web.Services;

/// <summary>
/// Retrieves comprehensive information about a file for the Inspect view.
/// Runs parallel queries for metadata, nodes, edges, and annotations.
/// </summary>
internal sealed class InspectService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<InspectService> _logger;

    public InspectService(RepoQlConnectionManager connectionManager, ILogger<InspectService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<InspectResult> InspectAsync(string uri, CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Run queries in parallel for performance
        var metadataTask = GetMetadataAsync(client, uri, cancellationToken);
        var nodesTask = GetNodesAsync(client, uri, cancellationToken);
        var outgoingEdgesTask = GetOutgoingEdgesAsync(client, uri, cancellationToken);
        var incomingEdgesTask = GetIncomingEdgesAsync(client, uri, cancellationToken);
        var annotationsTask = GetAnnotationsAsync(client, uri, cancellationToken);

        await Task.WhenAll(metadataTask, nodesTask, outgoingEdgesTask, incomingEdgesTask, annotationsTask).ConfigureAwait(false);

        return new InspectResult(
            Metadata: await metadataTask,
            Nodes: await nodesTask,
            OutgoingEdges: await outgoingEdgesTask,
            IncomingEdges: await incomingEdgesTask,
            Annotations: await annotationsTask
        );
    }

    private async Task<FileMetadata?> GetMetadataAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var sql = $@"
                SELECT
                    uri,
                    lang,
                    lines,
                    COALESCE(headline, '') as headline,
                    COALESCE(summary, '') as summary,
                    COALESCE(structure, '') as structure,
                    error_count,
                    warning_count,
                    has_embeddings
                FROM Files
                WHERE uri = '{EscapeSql(uri)}'
                LIMIT 1";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 1, cancellationToken: ct).ConfigureAwait(false);

            if (result.Rows.Count == 0)
                return null;

            var row = result.Rows[0];
            return new FileMetadata(
                Uri: GetString(row, 0),
                Language: GetString(row, 1),
                Lines: GetInt(row, 2),
                Headline: GetString(row, 3),
                Summary: GetString(row, 4),
                Structure: GetString(row, 5),
                ErrorCount: GetInt(row, 6),
                WarningCount: GetInt(row, 7),
                HasEmbeddings: GetBool(row, 8)
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get metadata for {Uri}", uri);
            return null;
        }
    }

    private async Task<IReadOnlyList<NodeInfo>> GetNodesAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var sql = $@"
                SELECT
                    n.kind,
                    COALESCE(n.name, '') as name,
                    COALESCE(n.qualified_name, '') as qualified_name,
                    s.start_line,
                    s.end_line
                FROM node n
                JOIN artifact a ON n.artifact_id = a.id
                LEFT JOIN span s ON n.id = s.node_id
                WHERE a.uri = '{EscapeSql(uri)}'
                  AND n.scope IN ('class', 'interface', 'struct', 'enum', 'function', 'method', 'property')
                ORDER BY s.start_line, n.kind, n.name
                LIMIT 100";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 100, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new NodeInfo(
                Kind: GetString(row, 0),
                Name: GetString(row, 1),
                QualifiedName: GetString(row, 2),
                StartLine: GetIntOrNull(row, 3),
                EndLine: GetIntOrNull(row, 4)
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get nodes for {Uri}", uri);
            return [];
        }
    }

    private async Task<IReadOnlyList<EdgeInfo>> GetOutgoingEdgesAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var sql = $@"
                SELECT
                    e.type,
                    dest.uri as target_uri,
                    COALESCE(dest_a.headline, '') as target_headline,
                    s.start_line as source_line
                FROM edge e
                JOIN node src ON e.source_node_id = src.id
                JOIN artifact src_a ON src.artifact_id = src_a.id
                JOIN node dest ON e.destination_node_id = dest.id
                LEFT JOIN artifact dest_a ON dest.artifact_id = dest_a.id
                LEFT JOIN span s ON src.id = s.node_id
                WHERE src_a.uri = '{EscapeSql(uri)}'
                ORDER BY e.type, s.start_line
                LIMIT 50";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 50, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new EdgeInfo(
                Type: GetString(row, 0),
                TargetUri: GetString(row, 1),
                TargetHeadline: GetString(row, 2),
                SourceLine: GetIntOrNull(row, 3)
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get outgoing edges for {Uri}", uri);
            return [];
        }
    }

    private async Task<IReadOnlyList<EdgeInfo>> GetIncomingEdgesAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var sql = $@"
                SELECT
                    e.type,
                    src_a.uri as source_uri,
                    COALESCE(src_a.headline, '') as source_headline,
                    s.start_line as source_line
                FROM edge e
                JOIN node dest ON e.destination_node_id = dest.id
                JOIN artifact dest_a ON dest.artifact_id = dest_a.id
                JOIN node src ON e.source_node_id = src.id
                JOIN artifact src_a ON src.artifact_id = src_a.id
                LEFT JOIN span s ON src.id = s.node_id
                WHERE dest_a.uri = '{EscapeSql(uri)}'
                ORDER BY e.type, src_a.uri
                LIMIT 50";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 50, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new EdgeInfo(
                Type: GetString(row, 0),
                TargetUri: GetString(row, 1),
                TargetHeadline: GetString(row, 2),
                SourceLine: GetIntOrNull(row, 3)
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get incoming edges for {Uri}", uri);
            return [];
        }
    }

    private async Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var sql = $@"
                SELECT
                    severity,
                    COALESCE(rule_id, '') as rule_id,
                    message,
                    start_line
                FROM Annotations
                WHERE resolved_target_uri = '{EscapeSql(uri)}'
                ORDER BY
                    CASE severity
                        WHEN 'error' THEN 1
                        WHEN 'warning' THEN 2
                        WHEN 'info' THEN 3
                        ELSE 4
                    END,
                    start_line
                LIMIT 50";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 50, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new AnnotationInfo(
                Severity: GetString(row, 0),
                RuleId: GetString(row, 1),
                Message: GetString(row, 2),
                Line: GetIntOrNull(row, 3)
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get annotations for {Uri}", uri);
            return [];
        }
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static string GetString(Contracts.RowData row, int index)
    {
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
            ? value.StringValue
            : "";
    }

    private static int GetInt(Contracts.RowData row, int index)
    {
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : 0;
    }

    private static int? GetIntOrNull(Contracts.RowData row, int index)
    {
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : null;
    }

    private static bool GetBool(Contracts.RowData row, int index)
    {
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue && value.BoolValue;
    }
}

public sealed record InspectResult(
    FileMetadata? Metadata,
    IReadOnlyList<NodeInfo> Nodes,
    IReadOnlyList<EdgeInfo> OutgoingEdges,
    IReadOnlyList<EdgeInfo> IncomingEdges,
    IReadOnlyList<AnnotationInfo> Annotations);

public sealed record FileMetadata(
    string Uri,
    string Language,
    int Lines,
    string Headline,
    string Summary,
    string Structure,
    int ErrorCount,
    int WarningCount,
    bool HasEmbeddings);

public sealed record NodeInfo(
    string Kind,
    string Name,
    string QualifiedName,
    int? StartLine,
    int? EndLine);

public sealed record EdgeInfo(
    string Type,
    string TargetUri,
    string TargetHeadline,
    int? SourceLine);

public sealed record AnnotationInfo(
    string Severity,
    string RuleId,
    string Message,
    int? Line);
