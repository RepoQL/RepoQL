using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace RepoQL.Contracts;

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
    private readonly RepoQL.RepoQLClient _client;
    private readonly TimeSpan? _defaultTimeout;
    private readonly CancellationTokenSource _leaseCts = new();
    private AsyncClientStreamingCall<ClientLeaseBeat, ClientLeaseSummary>? _leaseCall;

    private RepoQlClient(GrpcChannel channel, TimeSpan? defaultTimeout)
    {
        Channel = channel;
        _client = new RepoQL.RepoQLClient(channel);
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
    public static RepoQlClient Create(RepoQlClientOptions? options = null)
    {
        options ??= new RepoQlClientOptions();

        var socketPath = options.SocketPath;
        var repoPath = RepoLocator.FindRepoRoot(options.RepositoryPath);
        if (string.IsNullOrWhiteSpace(socketPath))
            socketPath = ResolveSocketPath(repoPath);

        // Ensure server is up (autostart if enabled)
        EnsureServerRunning(socketPath!, repoPath, TimeSpan.FromMilliseconds(EnvironmentTimeout("REPOQL_START_TIMEOUT_MS", 30000)));

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    var endpoint = new UnixDomainSocketEndPoint(socketPath!);
                    await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    // Avoid TCP-specific options on AF_UNIX to support Windows UDS; safely ignore failures
                    try { socket.SendBufferSize = 64 * 1024; }
                    catch
                    {
                        // ignored
                    }

                    try { socket.ReceiveBufferSize = 64 * 1024; }
                    catch
                    {
                        // ignored
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
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

    private static void EnsureServerRunning(string socketPath, string repoPath, TimeSpan timeout)
    {
        if (CanConnect(socketPath)) return;

        if (!AutostartEnabled())
            throw new InvalidOperationException($"RepoQL host is not running, and autostart is disabled. Expected socket at {socketPath}");

        LaunchHost(repoPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            // Re-resolve socket path each iteration to pick up mapping file written by the host (WSL scenario)
            try { socketPath = ResolveSocketPath(repoPath); } catch { /* ignore and keep prior */ }
            if (CanConnect(socketPath) && HealthServing(socketPath)) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"RepoQL host did not become ready within {timeout.TotalMilliseconds} ms (socket: {socketPath})");
    }

    private static bool CanConnect(string socketPath)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch { return false; }
    }

    private static bool HealthServing(string socketPath)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
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

        // 1) Honor explicit override
        var exe = Environment.GetEnvironmentVariable("REPOQL_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(exe))
        {
            StartProcess(exe!, new[] { repoPath }, implicitEnv);
            return;
        }

        // 2) Prefer unified app in the same directory: repoql-app or repoql
        var baseDir = AppContext.BaseDirectory;
        var unifiedNames = new[] { "repoql-app", "repoql" };
        foreach (var name in unifiedNames)
        {
            var candidates = BuildExecutableCandidates(Path.Combine(baseDir, name));
            var found = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(found))
            {
                // Unified app expects: host serve [--repo <path>]
                var args = new List<string> { "host", "serve", "--repo", repoPath };
                StartProcess(found, args, implicitEnv);
                return;
            }
        }

        // 3) Fallback to legacy repoql-host
        var hostBase = Path.Combine(baseDir, "repoql-host");
        var hostExe = BuildExecutableCandidates(hostBase).FirstOrDefault(File.Exists) ?? "repoql-host";
        StartProcess(hostExe, new[] { repoPath }, implicitEnv);
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
        var leaseClient = new RepoQL.RepoQLClient(Channel);
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
}
