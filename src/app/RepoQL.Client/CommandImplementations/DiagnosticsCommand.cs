using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Commands;
using RepoQL.Client.Diagnostics;
using RepoQL.Client.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.Client.CommandImplementations;

/// <summary>
/// Purpose: Expose system health diagnostics plus indexing and cloud health commands.
/// Complexity: Thin wrapper over SelfTestRunner for full/fast checks plus SQL-backed
/// aggregation and text rendering for specialized diagnostics commands.
/// </summary>
[CommandClass]
internal sealed class DiagnosticsCommand
{
    private const int SectionRowLimit = 15;
    private const long StuckAgeThresholdSeconds = 60;
    private const long SlowDurationThresholdMs = 30_000;

    private const string SummarySql = "SELECT _registry_summary_internal('all')";
    private const string CloudDiagnosticsSql = "SELECT cloud_diagnostics() as diag";
    private const string StuckQueueSql = """
        SELECT uri, stage, status, age_seconds, size_bytes, mime_type
        FROM processing_queue()
        WHERE CAST(age_seconds AS BIGINT) > 60
        ORDER BY age_seconds DESC
        """;
    private const string PendingSql = "SELECT _indexer_pending_internal('all')";
    private const string FailedSql = "SELECT _indexer_errors_internal('all')";
    private const string StatusSql = "SELECT _indexer_status_internal('all')";

    private readonly SelfTestRunner _runner;
    private readonly IIndexDiagnosticsOperations _operations;

    public DiagnosticsCommand(
        SelfTestRunner runner,
        RepoQlClientProvider clientProvider)
        : this(
            runner,
            new DefaultIndexDiagnosticsOperations(clientProvider))
    {
    }

    internal DiagnosticsCommand(
        SelfTestRunner runner,
        IIndexDiagnosticsOperations operations)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    [Command("diagnostics", Description = "Run full system health diagnostics")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        return CommandResult.Success(await _runner.RunAsync(DiagnosticCollectionMode.Full, cancel));
    }

    [Command("diagnostics.fast", Description = "Run quick system health checks")]
    public async Task<CommandResult> ExecuteFast(CancellationToken cancel)
    {
        return CommandResult.Success(await _runner.RunAsync(DiagnosticCollectionMode.Fast, cancel));
    }

    [Command("diagnostics.index", Description = "Show indexing health from registry and queue")]
    public async Task<CommandResult> ExecuteIndex(CancellationToken cancel)
    {
        var lines = new List<string> { "Index diagnostics" };

        IRepoQlClient client;
        try
        {
            client = await _operations.GetClientAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendSection(lines, "Summary", [$"  Error: Failed to connect to host: {ex.Message}"]);
            return CommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        AppendSection(lines, "Summary", await BuildSummarySectionAsync(client, cancel).ConfigureAwait(false));

        var stuckSection = await BuildStuckSectionAsync(client, cancel).ConfigureAwait(false);
        if (stuckSection.Count > 0)
            AppendSection(lines, "Stuck files", stuckSection);

        var failedSection = await BuildFailedSectionAsync(client, cancel).ConfigureAwait(false);
        if (failedSection.Count > 0)
            AppendSection(lines, "Failed files", failedSection);

        var slowSection = await BuildSlowSectionAsync(client, cancel).ConfigureAwait(false);
        if (slowSection.Count > 0)
            AppendSection(lines, "Slow files", slowSection);

        var durationSection = await BuildDurationSectionAsync(client, cancel).ConfigureAwait(false);
        if (durationSection.Count > 0)
            AppendSection(lines, "Duration by extension", durationSection);

        return CommandResult.Success(string.Join(Environment.NewLine, lines));
    }

    [Command("diagnostics.cloud", Description = "Verify cloud authentication, inference, and embedding services")]
    public async Task<CommandResult> ExecuteCloud(CancellationToken cancel)
    {
        var lines = new List<string> { "Cloud diagnostics" };

        IRepoQlClient client;
        try
        {
            client = await _operations.GetClientAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendSection(lines, "Host", FormatKeyValueLines([new("Error", $"Failed to connect to host: {ex.Message}")]));
            return CommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        string? text;
        try
        {
            var response = await client.ExecuteRawQueryAsync(CloudDiagnosticsSql, cancellationToken: cancel).ConfigureAwait(false);
            text = GetScalarString(response);
        }
        catch (Exception ex)
        {
            AppendSection(lines, "Host", FormatKeyValueLines([new("Error", $"cloud_diagnostics() failed: {ex.Message}")]));
            return CommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppendSection(lines, "Host", FormatKeyValueLines([new("Error", "cloud_diagnostics() returned no data")]));
            return CommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        var values = ParseKeyValueText(text);
        if (values.Count == 0)
        {
            AppendSection(lines, "Host", FormatKeyValueLines([new("Error", text.Trim())]));
            return CommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        AppendSection(lines, "Authentication", FormatKeyValueLines(BuildAuthenticationSection(values)));
        AppendSection(lines, "Inference", FormatKeyValueLines(BuildInferenceSection(values)));
        AppendSection(lines, "Embeddings", FormatKeyValueLines(BuildEmbeddingsSection(values)));

        return CommandResult.Success(string.Join(Environment.NewLine, lines));
    }

    internal static string FormatDuration(double milliseconds)
    {
        var clampedMilliseconds = Math.Max(0, milliseconds);
        if (clampedMilliseconds < 1)
            return $"{clampedMilliseconds:0.#}ms";
        if (clampedMilliseconds < 1000)
            return $"{Math.Round(clampedMilliseconds):0}ms";

        var duration = TimeSpan.FromMilliseconds(clampedMilliseconds);
        if (duration.TotalSeconds < 60)
            return $"{duration.TotalSeconds:0.#}s";

        var roundedSeconds = (long)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        var hours = roundedSeconds / 3600;
        var minutes = (roundedSeconds % 3600) / 60;
        var seconds = roundedSeconds % 60;

        if (hours > 0)
            return $"{hours}h {minutes}m {seconds}s";

        return $"{minutes}m {seconds}s";
    }

    internal static IReadOnlyList<DurationDistribution> CalculateDurationDistribution(IEnumerable<IndexerStatusEntry> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        return statuses
            .Where(status => status.ProcessingDurationMs is not null)
            .GroupBy(status => GetExtension(status.Uri))
            .Select(group =>
            {
                var orderedDurations = group
                    .Select(status => status.ProcessingDurationMs ?? 0d)
                    .OrderBy(duration => duration)
                    .ToArray();

                if (orderedDurations.Length == 0)
                    return null;

                var total = orderedDurations.Sum();
                return new DurationDistribution(
                    Extension: group.Key,
                    MinMs: orderedDurations[0],
                    P5Ms: Percentile(orderedDurations, 0.05),
                    P50Ms: Percentile(orderedDurations, 0.50),
                    AvgMs: orderedDurations.Average(),
                    P95Ms: Percentile(orderedDurations, 0.95),
                    MaxMs: orderedDurations[^1],
                    TotalMs: total,
                    Count: orderedDurations.Length);
            })
            .Where(distribution => distribution is not null && distribution.MaxMs > 0)
            .OrderByDescending(distribution => distribution!.TotalMs)
            .ThenBy(distribution => distribution!.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(distribution => distribution!)
            .ToArray();
    }

    internal static Dictionary<string, string> ParseKeyValueText(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildAuthenticationSection(IReadOnlyDictionary<string, string> values)
    {
        var authStatus = values.GetValueOrDefault("auth") ?? "unknown";
        if (!string.Equals(authStatus, "authenticated", StringComparison.OrdinalIgnoreCase))
        {
            var lines = new List<KeyValuePair<string, string>>
            {
                new("Status", authStatus)
            };

            var authError = values.GetValueOrDefault("auth_error");
            if (ShouldShowAuthAction(authError))
                lines.Add(new("Action", "command(command=\"auth.login\")"));
            else if (!string.IsNullOrWhiteSpace(authError))
                lines.Add(new("Error", authError));

            return lines;
        }

        var authenticated = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("identity")))
            authenticated.Add(new("Identity", values["identity"]));

        authenticated.Add(new("Token", values.GetValueOrDefault("token") ?? "valid"));
        return authenticated;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildInferenceSection(IReadOnlyDictionary<string, string> values)
    {
        var lines = new List<KeyValuePair<string, string>>
        {
            new("Status", values.GetValueOrDefault("inference") ?? "unknown")
        };

        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("inference_url")))
            lines.Add(new("Endpoint", values["inference_url"]));

        return lines;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildEmbeddingsSection(IReadOnlyDictionary<string, string> values)
    {
        var embeddingStatus = values.GetValueOrDefault("embedding") ?? "unknown";
        if (!string.Equals(embeddingStatus, "enabled", StringComparison.OrdinalIgnoreCase))
            return [new("Status", embeddingStatus)];

        var lines = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("embedding_provider")))
            lines.Add(new("Provider", values["embedding_provider"]));

        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("embedding_model")))
        {
            var model = values["embedding_model"];
            var dimension = values.TryGetValue("embedding_dimension", out var dimensionText)
                            && int.TryParse(dimensionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDimension)
                ? parsedDimension
                : 0;
            lines.Add(new("Model", dimension > 0 ? $"{model} ({dimension:N0} dim)" : model));
        }

        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("embedding_url")))
            lines.Add(new("Endpoint", values["embedding_url"]));

        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("embedding_reachable")))
            lines.Add(new("Reachable", values["embedding_reachable"]));

        var progress = BuildEmbeddingProgress(values);
        if (!string.IsNullOrWhiteSpace(progress))
            lines.Add(new("Progress", progress));

        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("embedding_error")))
            lines.Add(new("Error", values["embedding_error"]));

        return lines.Count == 0 ? [new("Status", embeddingStatus)] : lines;
    }

    private static bool ShouldShowAuthAction(string? authError)
        => !string.IsNullOrWhiteSpace(authError)
           && (authError.Contains("auth.login", StringComparison.OrdinalIgnoreCase)
               || authError.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
               || authError.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
               || authError.Contains("session expired", StringComparison.OrdinalIgnoreCase));

    private static string? BuildEmbeddingProgress(IReadOnlyDictionary<string, string> values)
    {
        if (!TryGetInt(values, "embedded_files", out var embeddedFiles) ||
            !TryGetInt(values, "total_files", out var totalFiles))
            return null;

        var progress = $"{embeddedFiles:N0}/{totalFiles:N0} embedded";
        if (TryGetInt(values, "not_applicable", out var notApplicable))
            progress += $" ({notApplicable:N0} not applicable)";

        return progress;
    }

    private static async Task<List<string>> BuildSummarySectionAsync(IRepoQlClient client, CancellationToken cancel)
    {
        try
        {
            var response = await client.ExecuteRawQueryAsync(SummarySql, cancellationToken: cancel).ConfigureAwait(false);
            var summary = ParseJsonArray<RegistrySummaryEntry>(response, "summary").FirstOrDefault();
            if (summary is null)
                return [$"  Error: Summary query returned no data."];

            var pending = summary.Discovered + summary.Indexing;
            return
            [
                $"  {summary.TotalFiles:N0} files | {summary.Indexed:N0} indexed | {pending:N0} pending | {summary.Failed:N0} failed | {summary.Stale:N0} stale"
            ];
        }
        catch (Exception ex)
        {
            return [$"  Error: {ex.Message}"];
        }
    }

    private static async Task<List<string>> BuildStuckSectionAsync(IRepoQlClient client, CancellationToken cancel)
    {
        List<StuckRow> queueRows = [];
        List<StuckRow> discoveredRows = [];
        var errors = new List<string>();

        try
        {
            var queueResponse = await client.ExecuteRawQueryAsync(StuckQueueSql, cancellationToken: cancel).ConfigureAwait(false);
            queueRows = ParseStuckQueueRows(queueResponse);
        }
        catch (Exception ex)
        {
            errors.Add($"  Error: processing_queue() failed: {ex.Message}");
        }

        try
        {
            var pendingResponse = await client.ExecuteRawQueryAsync(PendingSql, cancellationToken: cancel).ConfigureAwait(false);
            discoveredRows = ParseJsonArray<PendingEntry>(pendingResponse, "pending")
                .Where(entry => entry.Status.Equals("Discovered", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Uri, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new StuckRow("n/a", "registry", entry.Status, entry.Uri))
                .ToList();
        }
        catch (Exception ex)
        {
            errors.Add($"  Error: _indexer_pending_internal('all') failed: {ex.Message}");
        }

        var combinedRows = queueRows
            .Concat(discoveredRows)
            .ToList();

        if (combinedRows.Count == 0 && errors.Count == 0)
            return [];

        var lines = new List<string>();
        if (combinedRows.Count > 0)
        {
            var visibleRows = combinedRows
                .Take(SectionRowLimit)
                .Select(row => new[] { row.Age, row.Stage, row.Status, row.Uri })
                .ToList();

            lines.AddRange(FormatTable(
                headers: ["Age", "Stage", "Status", "Uri"],
                rows: visibleRows,
                rightAlignedColumns: [false, false, false, false]));

            AppendOverflow(lines, combinedRows.Count);
        }

        lines.AddRange(errors);
        return lines;
    }

    private static async Task<List<string>> BuildFailedSectionAsync(IRepoQlClient client, CancellationToken cancel)
    {
        try
        {
            var response = await client.ExecuteRawQueryAsync(FailedSql, cancellationToken: cancel).ConfigureAwait(false);
            var failures = ParseJsonArray<IndexerErrorEntry>(response, "failed files")
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Uri))
                .OrderBy(entry => entry.Uri, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (failures.Count == 0)
                return [];

            var visibleRows = failures
                .Take(SectionRowLimit)
                .Select(entry => new[] { entry.Uri, entry.Error ?? "(no error message)" })
                .ToList();

            var lines = FormatTable(
                headers: ["Uri", "Error"],
                rows: visibleRows,
                rightAlignedColumns: [false, false]);

            AppendOverflow(lines, failures.Count);
            return lines;
        }
        catch (Exception ex)
        {
            return [$"  Error: {ex.Message}"];
        }
    }

    private static async Task<List<string>> BuildSlowSectionAsync(IRepoQlClient client, CancellationToken cancel)
    {
        try
        {
            var response = await client.ExecuteRawQueryAsync(StatusSql, cancellationToken: cancel).ConfigureAwait(false);
            var slowRows = ParseJsonArray<IndexerStatusEntry>(response, "status")
                .Where(entry => entry.ProcessingDurationMs > SlowDurationThresholdMs)
                .OrderByDescending(entry => entry.ProcessingDurationMs)
                .ThenBy(entry => entry.Uri, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (slowRows.Count == 0)
                return [];

            var visibleRows = slowRows
                .Take(SectionRowLimit)
                .Select(entry => new[]
                {
                    FormatDuration(entry.ProcessingDurationMs ?? 0),
                    entry.Status,
                    entry.Uri
                })
                .ToList();

            var lines = FormatTable(
                headers: ["Duration", "Status", "Uri"],
                rows: visibleRows,
                rightAlignedColumns: [true, false, false]);

            AppendOverflow(lines, slowRows.Count);
            return lines;
        }
        catch (Exception ex)
        {
            return [$"  Error: {ex.Message}"];
        }
    }

    private static async Task<List<string>> BuildDurationSectionAsync(IRepoQlClient client, CancellationToken cancel)
    {
        try
        {
            var response = await client.ExecuteRawQueryAsync(StatusSql, cancellationToken: cancel).ConfigureAwait(false);
            var statuses = ParseJsonArray<IndexerStatusEntry>(response, "status");
            var distributions = CalculateDurationDistribution(statuses);

            if (distributions.Count == 0)
                return [];

            return FormatTable(
                headers: ["Ext", "Min", "P5", "P50", "Avg", "P95", "Max", "Total", "Count"],
                rows: distributions.Select(distribution => new[]
                {
                    distribution.Extension,
                    FormatDuration(distribution.MinMs),
                    FormatDuration(distribution.P5Ms),
                    FormatDuration(distribution.P50Ms),
                    FormatDuration(distribution.AvgMs),
                    FormatDuration(distribution.P95Ms),
                    FormatDuration(distribution.MaxMs),
                    FormatDuration(distribution.TotalMs),
                    distribution.Count.ToString("N0", CultureInfo.InvariantCulture)
                }),
                rightAlignedColumns: [false, true, true, true, true, true, true, true, true]);
        }
        catch (Exception ex)
        {
            return [$"  Error: {ex.Message}"];
        }
    }

    private static IEnumerable<string> FormatKeyValueLines(IEnumerable<KeyValuePair<string, string>> values)
    {
        var materialized = values.ToList();
        if (materialized.Count == 0)
            return [];

        var width = materialized.Max(pair => pair.Key.Length);
        return materialized.Select(pair => $"  {pair.Key.PadRight(width)}: {pair.Value}");
    }

    private static void AppendSection(List<string> lines, string title, IEnumerable<string> sectionLines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sectionLines);

        lines.Add(string.Empty);
        lines.Add(title);
        lines.AddRange(sectionLines);
    }

    private static List<StuckRow> ParseStuckQueueRows(RawQueryResponse response)
    {
        var columnIndex = BuildColumnIndex(response.Columns);
        var rows = new List<StuckRow>(response.Rows.Count);

        foreach (var row in response.Rows)
        {
            var ageSeconds = GetLong(row, columnIndex, "age_seconds");
            if (ageSeconds <= StuckAgeThresholdSeconds)
                continue;

            rows.Add(new StuckRow(
                Age: FormatDuration(ageSeconds * 1000),
                Stage: GetString(row, columnIndex, "stage") ?? "unknown",
                Status: GetString(row, columnIndex, "status") ?? "unknown",
                Uri: GetString(row, columnIndex, "uri") ?? "<unknown>"));
        }

        return rows;
    }

    private static List<T> ParseJsonArray<T>(RawQueryResponse response, string description)
    {
        var json = GetScalarString(response);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var data = JsonSerializer.Deserialize<List<T>>(json, JsonOptions);
            return data ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse {description} JSON: {ex.Message}", ex);
        }
    }

    private static string? GetScalarString(RawQueryResponse response)
    {
        if (response.Rows.Count == 0 || response.Rows[0].Values.Count == 0)
            return null;

        var value = response.Rows[0].Values[0];
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };
    }

    private static Dictionary<string, int> BuildColumnIndex(IEnumerable<ColumnSchema> columns)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var column in columns)
            index[column.Name] = position++;

        return index;
    }

    private static string? GetString(RowData row, IReadOnlyDictionary<string, int> columns, string columnName)
    {
        if (!columns.TryGetValue(columnName, out var index) || index >= row.Values.Count)
            return null;

        var value = row.Values[index];
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };
    }

    private static long GetLong(RowData row, IReadOnlyDictionary<string, int> columns, string columnName)
    {
        if (!columns.TryGetValue(columnName, out var index) || index >= row.Values.Count)
            return 0;

        var value = row.Values[index];
        return value.KindCase switch
        {
            Value.KindOneofCase.NumberValue => (long)value.NumberValue,
            Value.KindOneofCase.StringValue => long.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static List<string> FormatTable(IReadOnlyList<string> headers, IEnumerable<string[]> rows, IReadOnlyList<bool> rightAlignedColumns)
    {
        var materializedRows = rows.ToList();
        var columnCount = headers.Count;
        var widths = new int[columnCount];

        for (var i = 0; i < columnCount; i++)
            widths[i] = headers[i].Length;

        foreach (var row in materializedRows)
        {
            for (var i = 0; i < columnCount && i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i]?.Length ?? 0);
        }

        var lines = new List<string>
        {
            "  " + FormatTableRow(headers, widths, rightAlignedColumns),
            "  " + FormatTableRow(widths.Select(width => new string('-', width)).ToArray(), widths, rightAlignedColumns)
        };

        foreach (var row in materializedRows)
            lines.Add("  " + FormatTableRow(row, widths, rightAlignedColumns));

        return lines;
    }

    private static string FormatTableRow(IReadOnlyList<string> columns, IReadOnlyList<int> widths, IReadOnlyList<bool> rightAlignedColumns)
    {
        var cells = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var value = columns[i] ?? string.Empty;
            cells[i] = rightAlignedColumns[i]
                ? value.PadLeft(widths[i])
                : value.PadRight(widths[i]);
        }

        return string.Join("  ", cells);
    }

    private static void AppendOverflow(List<string> lines, int totalCount)
    {
        if (totalCount > SectionRowLimit)
            lines.Add($"  ... and {totalCount - SectionRowLimit:N0} more");
    }

    private static string GetExtension(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return "(none)";

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            var extension = Path.GetExtension(parsed.AbsolutePath);
            return string.IsNullOrWhiteSpace(extension) ? "(none)" : extension;
        }

        var fallbackExtension = Path.GetExtension(uri);
        return string.IsNullOrWhiteSpace(fallbackExtension) ? "(none)" : fallbackExtension;
    }

    private static double Percentile(IReadOnlyList<double> orderedDurations, double percentile)
    {
        if (orderedDurations.Count == 0)
            return 0;

        if (orderedDurations.Count == 1)
            return orderedDurations[0];

        var clampedPercentile = Math.Clamp(percentile, 0d, 1d);
        var position = clampedPercentile * (orderedDurations.Count - 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);

        if (lowerIndex == upperIndex)
            return orderedDurations[lowerIndex];

        var fraction = position - lowerIndex;
        return orderedDurations[lowerIndex] +
               ((orderedDurations[upperIndex] - orderedDurations[lowerIndex]) * fraction);
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        if (values.TryGetValue(key, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed record DurationDistribution(
        string Extension,
        double MinMs,
        double P5Ms,
        double P50Ms,
        double AvgMs,
        double P95Ms,
        double MaxMs,
        double TotalMs,
        int Count);

    internal sealed record IndexerStatusEntry(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("processing_duration_ms")] double? ProcessingDurationMs);

    private sealed record RegistrySummaryEntry(
        [property: JsonPropertyName("total_files")] int TotalFiles,
        [property: JsonPropertyName("discovered")] int Discovered,
        [property: JsonPropertyName("indexing")] int Indexing,
        [property: JsonPropertyName("indexed")] int Indexed,
        [property: JsonPropertyName("failed")] int Failed,
        [property: JsonPropertyName("stale")] int Stale,
        [property: JsonPropertyName("embedded")] int Embedded,
        [property: JsonPropertyName("not_applicable")] int NotApplicable);

    private sealed record PendingEntry(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("status")] string Status);

    private sealed record IndexerErrorEntry(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record StuckRow(string Age, string Stage, string Status, string Uri);

    internal interface IIndexDiagnosticsOperations
    {
        ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken);
    }

    private sealed class DefaultIndexDiagnosticsOperations(RepoQlClientProvider clientProvider) : IIndexDiagnosticsOperations
    {
        public ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken)
            => clientProvider.GetClientAsync(cancellationToken);
    }
}

