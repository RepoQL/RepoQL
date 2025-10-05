using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Grpc.Core;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Host.Options;

namespace RepoQL.Host.Services;

public sealed class RepoQlServiceImpl(IGraphStore store, RepositoryConfiguration repoConfig, IInitialIndexingBarrier barrier) : Contracts.RepoQL.RepoQLBase
{
    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    public override async Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        // Hold queries until initial indexing completes
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var resp = new RawQueryResponse();
        try
        {
            var parameters = request.Parameters.Select(FromProtoValue).ToArray();
            var rows = store.RawQuery(request.Sql, parameters);

            var limited = request.Limit > 0;
            var take = limited ? rows.Take(request.Limit) : rows;

            IReadOnlyDictionary<string, object?>? first = null;
            foreach (var r in take)
            {
                if (first is null)
                {
                    first = r;
                    foreach (var col in first.Keys)
                    {
                        var sample = first[col];
                        resp.Columns.Add(new ColumnSchema { Name = col, DbType = InferDbType(sample) });
                    }
                }
                var rd = new RowData();
                foreach (var col in first.Keys)
                {
                    r.TryGetValue(col, out var value);
                    rd.Values.Add(ToProtoValue(value));
                }
                resp.Rows.Add(rd);
            }
            resp.RowCount = resp.Rows.Count;
            resp.Truncated = limited && (first is not null) && (store.RawQuery(request.Sql, parameters).Skip(resp.Rows.Count).Any());
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        return resp;
    }

    public override async Task<ClientLeaseSummary> HoldClientLease(IAsyncStreamReader<ClientLeaseBeat> requestStream, ServerCallContext context)
    {
        // Accept beats until the client finishes or the call is cancelled. Update lease registry.
        string? clientId = null;
        try
        {
            var started = DateTime.UtcNow;
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var beat = requestStream.Current;
                clientId ??= string.IsNullOrWhiteSpace(beat.ClientId) ? Guid.NewGuid().ToString() : beat.ClientId;
                var beatAt = ParseRfc3339OrUtcNow(beat.BeatAt);
                LeaseRegistry.Upsert(clientId, beatAt);
            }
        }
        catch (OperationCanceledException)
        {
            // client cancelled
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(clientId))
                LeaseRegistry.Remove(clientId!);
        }

        // Return a summary snapshot
        var state = context.GetHttpContext().RequestServices.GetRequiredService<HostState>();
        return new ClientLeaseSummary
        {
            ServerStartedAt = state.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ImplicitStart = state.ImplicitStart,
            ActiveClients = LeaseRegistry.Count,
            ShutdownAfterIdleSeconds = GetEnvInt("REPOQL_IDLE_GRACE_SECONDS", 45)
        };
    }

    private static DateTime ParseRfc3339OrUtcNow(string s)
    {
        if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static Struct ParseStruct(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ToStruct(doc.RootElement);
    }

    private static Struct ToStruct(JsonElement el)
    {
        var s = new Struct();
        if (el.ValueKind != JsonValueKind.Object) return s;
        foreach (var prop in el.EnumerateObject())
        {
            s.Fields[prop.Name] = ToValue(prop.Value);
        }
        return s;
    }

    private static ListValue ToList(JsonElement el)
    {
        var list = new ListValue();
        foreach (var item in el.EnumerateArray())
            list.Values.Add(ToValue(item));
        return list;
    }

    private static Value ToValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => Value.ForNull(),
            JsonValueKind.True => Value.ForBool(true),
            JsonValueKind.False => Value.ForBool(false),
            JsonValueKind.Number => el.TryGetInt64(out var l)
                ? Value.ForNumber(l)
                : Value.ForNumber(el.GetDouble()),
            JsonValueKind.String => Value.ForString(el.GetString() ?? string.Empty),
            JsonValueKind.Object => Value.ForStruct(ToStruct(el)),
            JsonValueKind.Array => Value.ForList([.. ToList(el).Values]),
            _ => Value.ForNull()
        };
    }

    public override async Task ExecuteRawQueryStream(RawQueryRequest request, IServerStreamWriter<RawQueryRow> responseStream, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var parameters = request.Parameters.Select(FromProtoValue).ToArray();
        var rows = store.RawQuery(request.Sql, parameters);
        var limited = request.Limit > 0;
        var sentSchema = false;

        foreach (var r in limited ? rows.Take(request.Limit) : rows)
        {
            var msg = new RawQueryRow();
            if (!sentSchema)
            {
                foreach (var col in r.Keys)
                {
                    msg.Columns.Add(new ColumnSchema { Name = col, DbType = InferDbType(r[col]) });
                }
                sentSchema = true;
            }
            var rd = new RowData();
            foreach (var col in r.Keys)
            {
                r.TryGetValue(col, out var value);
                rd.Values.Add(ToProtoValue(value));
            }
            msg.Row = rd;
            await responseStream.WriteAsync(msg, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<GetDocumentSummariesResponse> GetDocumentSummaries(GetDocumentSummariesRequest request, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var resp = new GetDocumentSummariesResponse();
        var kindsCsv = (request.Kinds != null && request.Kinds.Count > 0)
            ? string.Join(",", request.Kinds.Select(s => s.Trim()).Where(s => s.Length > 0))
            : null;
        var minSeverity = string.IsNullOrWhiteSpace(request.MinSeverity) ? null : request.MinSeverity;

        foreach (var uriStr in request.Uris)
        {
            var result = new DocumentSummaryResult { Uri = uriStr, Status = SummaryStatus.NotFound };
            try
            {
                if (string.IsNullOrWhiteSpace(uriStr))
                {
                    result.Status = SummaryStatus.Error;
                    result.Error = "uri is required";
                    resp.Results.Add(result);
                    continue;
                }

                var canonicalUri = CanonicalizeRepositoryUri(uriStr, repoConfig.Path);

                // Use annotations_for macro to include resolved_target_uri
                const string sql = @"SELECT id, semantic_key, kind, severity, source, rule_id, message, data,
                                            resolved_target_uri, target_node_id, target_edge_id, target_span_id,
                                            created_at, expires_at
                                     FROM annotations_for(?, ?, ?);";
                var rowEnum = store.RawQuery(sql, canonicalUri, kindsCsv ?? (object)DBNull.Value, minSeverity ?? (object)DBNull.Value);
                var any = false;
                foreach (var row in rowEnum)
                {
                    any = true;
                    var ann = new SummaryAnnotation
                    {
                        Id = row.TryGetValue("id", out var id) && id is Guid gid ? gid.ToString() : row["id"]?.ToString() ?? string.Empty,
                        SemanticKey = row["semantic_key"]?.ToString() ?? string.Empty,
                        Kind = row["kind"]?.ToString() ?? string.Empty,
                        Severity = row["severity"]?.ToString() ?? string.Empty,
                        Source = row["source"]?.ToString() ?? string.Empty,
                        RuleId = row["rule_id"]?.ToString() ?? string.Empty
                    };

                    if (request.IncludeMessage)
                        ann.Message = row["message"]?.ToString() ?? string.Empty;

                    if (request.IncludeData)
                    {
                        try
                        {
                            var json = row["data"]?.ToString() ?? "{}";
                            ann.Data = ParseStruct(json);
                        }
                        catch
                        {
                            ann.Data = new Struct();
                        }
                    }

                    if (request.IncludeResolvedTargetUri)
                        ann.ResolvedTargetUri = row["resolved_target_uri"]?.ToString() ?? string.Empty;

                    // Optional targets
                    if (row.TryGetValue("target_node_id", out var tn) && tn != null)
                        ann.TargetNodeId = tn is Guid g1 ? g1.ToString() : tn.ToString();
                    if (row.TryGetValue("target_edge_id", out var te) && te != null)
                        ann.TargetEdgeId = te is Guid g2 ? g2.ToString() : te.ToString();
                    if (row.TryGetValue("target_span_id", out var ts) && ts != null)
                        ann.TargetSpanId = ts is Guid g3 ? g3.ToString() : ts.ToString();

                    if (row.TryGetValue("created_at", out var ca) && ca is DateTime cadt)
                        ann.CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(cadt, DateTimeKind.Utc));
                    if (row.TryGetValue("expires_at", out var ea) && ea is DateTime eadt)
                        ann.ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(eadt, DateTimeKind.Utc));

                    result.Annotations.Add(ann);
                }
                if (!any)
                {
                    // Distinguish not-found from empty annotations
                    const string existsSql = "SELECT 1 FROM node WHERE lower(uri)=lower(repository_uri_container(?)) LIMIT 1";
                    var exists = store.RawQuery(existsSql, canonicalUri).Any();
                    result.Status = exists ? SummaryStatus.Ok : SummaryStatus.NotFound;
                }
                else
                {
                    result.Status = SummaryStatus.Ok;
                }
            }
            catch (Exception ex)
            {
                result.Status = SummaryStatus.Error;
                result.Error = ex.Message;
            }
            resp.Results.Add(result);
        }

        return resp;
    }

    private static string CanonicalizeRepositoryUri(string input, string repoRoot)
    {
        // Ensure we use repo-aware file:///relative when input is under the repo root
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = u.LocalPath; // OS path (may begin with '/')
            // Treat file:///relative as repo-aware relative
            if (string.IsNullOrEmpty(u.Host))
            {
                var relOnly = localPath.TrimStart('/', '\\');
                if (!string.IsNullOrEmpty(relOnly))
                {
                    return $"file:///{relOnly.Replace('\\', '/')}";
                }
            }
            try
            {
                var root = Path.GetFullPath(repoRoot);
                var full = Path.GetFullPath(localPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                    return $"file:///{rel}";
                }
            }
            catch { /* fall through */ }
            return u.AbsoluteUri; // not under repo
        }
        return input;
    }

    private static object? FromProtoValue(Value v)
    {
        return v.KindCase switch
        {
            Value.KindOneofCase.NullValue => DBNull.Value,
            Value.KindOneofCase.BoolValue => v.BoolValue,
            Value.KindOneofCase.NumberValue => v.NumberValue,
            Value.KindOneofCase.StringValue => v.StringValue,
            Value.KindOneofCase.StructValue => JsonFormatter.Default.Format(v),
            Value.KindOneofCase.ListValue => JsonFormatter.Default.Format(v),
            _ => DBNull.Value
        };
    }

    private static Value ToProtoValue(object? value)
    {
        return value switch
        {
            null => Value.ForNull(),
            DBNull => Value.ForNull(),
            bool b => Value.ForBool(b),
            byte b => Value.ForNumber(b),
            sbyte sb => Value.ForNumber(sb),
            short s => Value.ForNumber(s),
            ushort us => Value.ForNumber(us),
            int i => Value.ForNumber(i),
            uint ui => Value.ForNumber(ui),
            long l => Value.ForNumber(l),
            ulong ul => Value.ForNumber((double)ul),
            float f => Value.ForNumber(f),
            double d => Value.ForNumber(d),
            decimal dec => Value.ForNumber((double)dec),
            string s => Value.ForString(s),
            DateTime dt => Value.ForString(dt.ToString("O")),
            Guid g => Value.ForString(g.ToString()),
            byte[] bytes => Value.ForString(Convert.ToBase64String(bytes)),
            _ => Value.ForString(value.ToString() ?? string.Empty)
        };
    }

    private static string InferDbType(object? sample)
    {
        if (sample is null || sample is DBNull) return "UNKNOWN";
        return sample switch
        {
            bool => "BOOLEAN",
            byte or sbyte or short or ushort or int => "INTEGER",
            uint or long => "BIGINT",
            ulong => "UBIGINT",
            float or double or decimal => "DOUBLE",
            DateTime => "TIMESTAMP",
            Guid => "UUID",
            byte[] => "BLOB",
            _ => "VARCHAR"
        };
    }
}
