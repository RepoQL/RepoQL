using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using RepoQL.Contracts;
using ProtoPipelineSnapshot = RepoQL.Contracts.PipelineSnapshot;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;

namespace RepoQL.Protocol;

/// <summary>
/// Simple gRPC client wrapper for the RepoQL service using a Unix domain socket transport.
/// </summary>
/// <remarks>
/// Socket discovery mirrors the server: prefer ".repoql/socket.path" mapping (WSL Windows mount case),
/// otherwise use ".repoql/repoql.sock" under the repository root discovered by <see cref="RepoLocator.FindRepoRoot"/>.
/// </remarks>
public sealed class RepoQlClient : IRepoQlClient
{
    public GrpcChannel Channel { get; }
    private readonly Contracts.RepoQL.RepoQLClient _client;
    private readonly TimeSpan? _defaultTimeout;
    private readonly CancellationTokenSource _leaseCts = new();
    private AsyncClientStreamingCall<ClientLeaseBeat, ClientLeaseSummary>? _leaseCall;

    private RepoQlClient(GrpcChannel channel, TimeSpan? defaultTimeout)
    {
        Channel = channel;
        _client = new Contracts.RepoQL.RepoQLClient(channel);
        _defaultTimeout = defaultTimeout;
    }

    /// <summary>
    /// Create a client from an existing <see cref="GrpcChannel"/> (useful for in-memory tests with TestServer).
    /// </summary>
    public static RepoQlClient FromChannel(GrpcChannel channel, TimeSpan? defaultTimeout = null)
        => new RepoQlClient(channel, defaultTimeout);

    /// <summary>
    /// Create a client connected to the repository's RepoQL server over a Unix domain socket.
    /// </summary>
    /// <param name="options">Optional configuration for socket discovery and default timeouts.</param>
    /// <param name="cancellationToken"></param>
    public static async Task<RepoQlClient> CreateAsync(RepoQlClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RepoQlClientOptions();

        var socketPath = options.SocketPath;
        var repoPath = RepoLocator.FindRepoRoot(options.RepositoryPath);
        if (string.IsNullOrWhiteSpace(socketPath))
            socketPath = ResolveSocketPath(repoPath);

        // Ensure server is up (autostart if enabled)
        var finalSocketPath = await EnsureServerRunning(socketPath!, repoPath, TimeSpan.FromMilliseconds(EnvironmentTimeout("REPOQL_START_TIMEOUT_MS", 30000)), cancellationToken);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await s.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(finalSocketPath), ct).ConfigureAwait(false);
                try { s.SendBufferSize = 64 * 1024; } catch { }
                try { s.ReceiveBufferSize = 64 * 1024; } catch { }
                return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true
        };

        var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Insecure // plaintext over UDS
        });

        var client = new RepoQlClient(channel, options.DefaultTimeout);
        // Establish required client lease before returning (no backwards-compat fallbacks)
        client.EstablishLeaseOrThrow(repoPath, TimeSpan.FromMilliseconds(EnvironmentTimeout("REPOQL_LEASE_START_TIMEOUT_MS", 5000)));
        return client;
    }

    private static async Task<string> EnsureServerRunning(string socketPath, string repoPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var s = await ConnectAsync(socketPath, cancellationToken);
        if (s != null)
        {
            var ok = HealthServing(s);
            try { s.Dispose(); } catch { }
            if (ok) return socketPath;
        }

        if (!AutostartEnabled())
            throw new InvalidOperationException($"RepoQL host is not running, and autostart is disabled. Expected socket at {socketPath}");

        LaunchHost(repoPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            // Re-resolve socket path each iteration to pick up mapping file written by the host (WSL scenario)
            try { socketPath = ResolveSocketPath(repoPath); } catch { /* ignore and keep prior */ }

            var socket = await ConnectAsync(socketPath, cancellationToken);
            if (socket != null && HealthServing(socket))
            {
                try { socket.Dispose(); } catch { }
                return socketPath;
            }
            try { socket?.Dispose(); } catch { }
            await Task.Delay(TimeSpan.FromSeconds(0.1), cancellationToken);
        }
        throw new TimeoutException($"RepoQL host did not become ready within {timeout.TotalMilliseconds} ms (socket: {socketPath})");
    }

    private static async Task<Socket?> ConnectAsync(string socketPath, CancellationToken cancellationToken)
    {
        try
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return s;
        }
        catch { return null; }
    }

    private static bool HealthServing(Socket socket)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (_, _) => new ValueTask<Stream>(new NetworkStream(socket, ownsSocket: false))
            };
            using var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions { HttpHandler = handler, Credentials = ChannelCredentials.Insecure });
            var hc = new Grpc.Health.V1.Health.HealthClient(channel);
            var resp = hc.Check(new Grpc.Health.V1.HealthCheckRequest { Service = "repoql.v1.RepoQL" });
            return resp.Status == Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving;
        }
        catch
        {
            return false;
        }
    }

    private static void LaunchHost(string repoPath)
    {
        var implicitEnv = new Dictionary<string, string?>
        {
            ["REPOQL_IMPLICIT"] = "1"
        };
        
        var baseDir = AppContext.BaseDirectory;
        var args = new List<string> { "serve", "--repository", repoPath, "--implicit-start" };
        StartProcess(BuildExecutableCandidates(Path.Join(baseDir, "repoql")).FirstOrDefault(File.Exists) ?? "repoql" ,args, implicitEnv);
    }

    private static IEnumerable<string> BuildExecutableCandidates(string basePath)
    {
        if (OperatingSystem.IsWindows())
            yield return basePath + ".exe";
        yield return basePath + ".dll"; // dotnet <dll>
        yield return basePath;           // self-contained
    }

    private static void StartProcess(string exePathOrCommand, IEnumerable<string> args, IDictionary<string, string?> env)
    {
        string fileName;
        string arguments;
        if (exePathOrCommand.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments = "\"" + exePathOrCommand + "\" " + string.Join(' ', args.Select(a => "\"" + a + "\""));
        }
        else
        {
            fileName = exePathOrCommand;
            arguments = string.Join(' ', args.Select(a => "\"" + a + "\""));
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true
        };
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        try { System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { throw new InvalidOperationException($"Failed to launch RepoQL host using '{exePathOrCommand}'. Set REPOQL_HOST_PATH to override.", ex); }
    }

    private static bool AutostartEnabled()
        => !string.Equals(Environment.GetEnvironmentVariable("REPOQL_AUTOSTART"), "0", StringComparison.Ordinal);

    private static int EnvironmentTimeout(string name, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    private void EstablishLeaseOrThrow(string repoPath, TimeSpan timeout)
    {
        var leaseClient = new Contracts.RepoQL.RepoQLClient(Channel);
        _leaseCall = leaseClient.HoldClientLease(cancellationToken: _leaseCts.Token);

        var clientId = Guid.NewGuid().ToString();
        var pid = Environment.ProcessId;
        var tool = AppDomain.CurrentDomain.FriendlyName;
        var ver = typeof(RepoQlClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var startedAt = DateTime.UtcNow.ToString("O");

        // Send first beat and ensure it succeeds within timeout
        var firstBeat = _leaseCall.RequestStream.WriteAsync(new ClientLeaseBeat
        {
            ClientId = clientId,
            Pid = pid,
            Tool = tool,
            Version = ver,
            RepoPath = repoPath,
            StartedAt = startedAt,
            BeatAt = DateTime.UtcNow.ToString("O")
        });

        if (!firstBeat.Wait(timeout))
            throw new TimeoutException("Failed to establish RepoQL client lease within timeout.");
        if (firstBeat.IsFaulted)
            throw firstBeat.Exception?.InnerException ?? firstBeat.Exception!;

        // Start background beat loop
        _ = Task.Run(async () =>
        {
            while (!_leaseCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _leaseCts.Token).ConfigureAwait(false);
                await _leaseCall!.RequestStream.WriteAsync(new ClientLeaseBeat
                {
                    ClientId = clientId,
                    Pid = pid,
                    Tool = tool,
                    Version = ver,
                    RepoPath = repoPath,
                    StartedAt = startedAt,
                    BeatAt = DateTime.UtcNow.ToString("O")
                }).ConfigureAwait(false);
            }
        }, _leaseCts.Token);
    }

    /// <inheritdoc />
    public async Task<RawQueryResponse> ExecuteRawQueryAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        CancellationToken cancellationToken = default)
    {
        var req = new RawQueryRequest
        {
            Sql = sql,
            Limit = rowLimit.GetValueOrDefault(0)
        };
        foreach (var p in parameters ?? [])
            req.Parameters.Add(ToValue(p));

        var deadline = _defaultTimeout.HasValue ? DateTime.UtcNow + _defaultTimeout.Value : (DateTime?)null;
        return await _client.ExecuteRawQueryAsync(req, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawQueryRow> ExecuteRawQueryStreamAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var req = new RawQueryRequest
        {
            Sql = sql,
            Limit = rowLimit.GetValueOrDefault(0)
        };
        foreach (var p in parameters ?? [])
            req.Parameters.Add(ToValue(p));

        var deadline = _defaultTimeout.HasValue ? DateTime.UtcNow + _defaultTimeout.Value : (DateTime?)null;
        using var call = _client.ExecuteRawQueryStream(req, deadline: deadline, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return call.ResponseStream.Current;
        }
    }

    /// <inheritdoc />
    public async Task<GetDocumentSummariesResponse> GetDocumentSummariesAsync(
        IEnumerable<string> documentUris,
        IEnumerable<string>? annotationKinds = null,
        string? minimumSeverity = null,
        bool includeData = false,
        bool includeMessage = true,
        bool includeResolvedTargetUri = false,
        CancellationToken cancellationToken = default)
    {
        var req = new GetDocumentSummariesRequest
        {
            MinSeverity = minimumSeverity ?? string.Empty,
            IncludeData = includeData,
            IncludeMessage = includeMessage,
            IncludeResolvedTargetUri = includeResolvedTargetUri
        };
        foreach (var u in documentUris) if (!string.IsNullOrWhiteSpace(u)) req.Uris.Add(u);
        if (annotationKinds != null) foreach (var k in annotationKinds) if (!string.IsNullOrWhiteSpace(k)) req.Kinds.Add(k);

        var deadline = _defaultTimeout.HasValue ? DateTime.UtcNow + _defaultTimeout.Value : (DateTime?)null;
        return await _client.GetDocumentSummariesAsync(req, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try { _leaseCts.Cancel(); }
        catch
        {
            // ignored
        }

        Channel.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves the Unix Domain Socket path for RepoQL communication.
    /// </summary>
    /// <param name="repositoryPath">The repository root path</param>
    /// <returns>The socket path to use for gRPC communication</returns>
    /// <remarks>
    /// Socket resolution follows this priority:
    /// 1. If .repoql/socket.path exists, use the path it contains (WSL/cross-boundary scenario)
    /// 2. Otherwise use .repoql/repoql.sock (standard local socket)
    /// 
    /// The socket.path file is a WSL-specific workaround where Windows hosts create sockets
    /// in temp directories (/tmp/repoql/...) that need to be discoverable from WSL clients.
    /// </remarks>
    private static string ResolveSocketPath(string repositoryPath)
    {
        var repoPath = Path.GetFullPath(repositoryPath);
        var repoqlDir = Path.Combine(repoPath, ".repoql");

        // Check for socket mapping file (used in WSL/Windows cross-boundary scenarios)
        var mapFile = Path.Combine(repoqlDir, "socket.path");
        var socket = Path.Combine(repoqlDir, "repoql.sock");
        if (File.Exists(socket) || !File.Exists(mapFile))
            return socket;

        try
        {
            var mapped = File.ReadAllText(mapFile).Trim();
            return !string.IsNullOrWhiteSpace(mapped) 
                ? mapped 
                : socket;
        }
        catch
        {
            // Default: use local socket in .repoql directory
            return socket;
        }
    }

    private static Value ToValue(object? o)
    {
        return o switch
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
            string str => Value.ForString(str),
            Guid g => Value.ForString(g.ToString()),
            DateTime dt => Value.ForString(dt.ToString("O")),
            IEnumerable<string> list => new Value { ListValue = new ListValue { Values = { list.Select(Value.ForString) } } },
            _ => Value.ForString(o.ToString() ?? string.Empty)
        };
    }


    public async IAsyncEnumerable<ReindexProgress> ReindexAllAsync(bool clear = false, TimeSpan? timeout = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? _defaultTimeout);
        var call = _client.ReindexAll(new ReindexRequest { Clear = clear }, deadline: deadline, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task<ProtoPipelineSnapshot> WaitForPipelineAsync(
        IEnumerable<ProtoPipelineStage>? stages = null,
        bool waitAll = true,
        CancellationToken cancellationToken = default)
    {
        var request = new WaitForPipelineRequest { WaitAll = waitAll };
        if (stages is not null)
            request.Stages.AddRange(stages);

        var deadline = _defaultTimeout.HasValue ? DateTime.UtcNow + _defaultTimeout.Value : (DateTime?)null;
        var response = await _client.WaitForPipelineAsync(request, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        return response.Snapshot ?? new ProtoPipelineSnapshot();
    }
}
