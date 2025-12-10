using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using ProtoPipelineStatus = RepoQL.Contracts.PipelineStatus;
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
    private enum ConnectionMode
    {
        ExternalChannel,
        Managed
    }

    private readonly ConnectionMode _mode;
    private readonly RepoDirectoryAccessor? _repoDirectory;
    private readonly string? _configuredSocketPath;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private GrpcChannel? _channel;
    private Contracts.RepoQL.RepoQLClient? _client;
    private readonly TimeSpan? _defaultTimeout;
    private CancellationTokenSource? _leaseCts;
    private AsyncClientStreamingCall<ClientLeaseBeat, ClientLeaseSummary>? _leaseCall;
    private readonly ILogger _logger;

    public GrpcChannel Channel => _channel ?? throw new InvalidOperationException("RepoQL client is not connected.");

    private RepoQlClient(GrpcChannel channel, TimeSpan? defaultTimeout, ILogger<RepoQlClient>? logger = null)
    {
        _mode = ConnectionMode.ExternalChannel;
        _channel = channel;
        _client = new Contracts.RepoQL.RepoQLClient(channel);
        _defaultTimeout = defaultTimeout;
        _logger = logger ?? NullLogger<RepoQlClient>.Instance;
    }

    private RepoQlClient(RepoQlClientOptions options, string repoPath, string? socketPath, ILogger? logger = null)
    {
        _mode = ConnectionMode.Managed;
        _repoDirectory = new RepoDirectoryAccessor(repoPath);
        _configuredSocketPath = socketPath;
        _defaultTimeout = options.DefaultTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Create a client from an existing <see cref="GrpcChannel"/> (useful for in-memory tests with TestServer).
    /// </summary>
    public static RepoQlClient FromChannel(GrpcChannel channel, TimeSpan? defaultTimeout = null, ILogger<RepoQlClient>? logger = null)
        => new RepoQlClient(channel, defaultTimeout, logger);

    /// <summary>
    /// Create a client connected to the repository's RepoQL server over a Unix domain socket.
    /// </summary>
    /// <param name="options">Optional configuration for socket discovery and default timeouts.</param>
    /// <param name="cancellationToken"></param>
    public static async Task<IRepoQlClient> CreateAsync(RepoQlClientOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        options ??= new RepoQlClientOptions();
        if (!RepoLocator.TryFindRepoRoot(options.RepositoryPath, out var repoPath, out var searchedFrom))
        {
            throw new RepoRootNotFoundException(searchedFrom ?? Directory.GetCurrentDirectory());
        }

        logger ??= NullLogger.Instance;
        logger.LogInformation("RepoQlClient: creating managed connection (repoRoot='{RepoRoot}', socketOverride='{SocketOverride}').",
            repoPath,
            options.SocketPath ?? "<null>");

        var client = new RepoQlClient(options, repoPath, options.SocketPath, logger);
        await client.EnsureConnectedAsync(forceReconnect: true, cancellationToken).ConfigureAwait(false);
        return client;
    }

    private async Task EnsureConnectedAsync(bool forceReconnect, CancellationToken cancellationToken)
    {
        if (_mode == ConnectionMode.ExternalChannel)
        {
            if (_client == null)
                throw new InvalidOperationException("RepoQL client was not initialized with a channel.");
            return;
        }

        if (!forceReconnect && _client != null)
            return;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceReconnect && _client != null)
                return;

            DisposeChannel();
            await ConnectManagedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectManagedAsync(CancellationToken cancellationToken)
    {
        if (_mode != ConnectionMode.Managed)
            throw new InvalidOperationException("Managed connection is not enabled for this client.");

        var repoDirectory = _repoDirectory ?? throw new InvalidOperationException("Repository path is not configured.");
        var allowReResolve = string.IsNullOrWhiteSpace(_configuredSocketPath);
        var socketPath = allowReResolve
            ? repoDirectory.ResolveSocketPath()
            : _configuredSocketPath!;
        _logger.LogInformation("RepoQlClient: connect (repoRoot='{RepoRoot}', initialSocket='{SocketPath}', allowReResolve={AllowReResolve}).",
            repoDirectory.RepoRoot,
            socketPath,
            allowReResolve);
        var finalSocketPath = await EnsureServerRunning(
            repoDirectory,
            socketPath,
            allowReResolve,
            TimeSpan.FromMilliseconds(EnvironmentTimeout("REPOQL_START_TIMEOUT_MS", 120_000)),
            cancellationToken).ConfigureAwait(false);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(finalSocketPath), ct).ConfigureAwait(false);
                try { s.SendBufferSize = 64 * 1024; } catch { }
                try { s.ReceiveBufferSize = 64 * 1024; } catch { }
                return new NetworkStream(s, ownsSocket: true);
            },
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true
        };

        _channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Insecure
        });

        _client = new Contracts.RepoQL.RepoQLClient(_channel);
        _leaseCts = new CancellationTokenSource();
        EstablishLeaseOrThrow(repoDirectory.RepoRoot, TimeSpan.FromMilliseconds(EnvironmentTimeout("REPOQL_LEASE_START_TIMEOUT_MS", 5000)));
    }

    private void DisposeChannel()
    {
        var leaseCts = Interlocked.Exchange(ref _leaseCts, null);
        if (leaseCts != null)
        {
            try { leaseCts.Cancel(); }
            catch { }
            leaseCts.Dispose();
        }

        _leaseCall = null;

        var channel = Interlocked.Exchange(ref _channel, null);
        channel?.Dispose();
        _client = null;
    }

    private async Task<T> InvokeWithReconnectAsync<T>(Func<Contracts.RepoQL.RepoQLClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var maxAttempts = _mode == ConnectionMode.Managed ? 2 : 1;
        Exception? last = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await EnsureConnectedAsync(forceReconnect: attempt > 0, cancellationToken).ConfigureAwait(false);
            var client = _client ?? throw new InvalidOperationException("RepoQL client is not connected.");

            try
            {
                return await call(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && _mode == ConnectionMode.Managed && ShouldAttemptReconnect(ex))
            {
                _logger.LogWarning(ex, "RepoQlClient: first attempt failed; disposing channel and retrying.");
                last = ex;
                DisposeChannel();
            }
        }

        throw last ?? new InvalidOperationException("RepoQL client operation failed.");
    }

    private static bool ShouldAttemptReconnect(Exception ex)
        => ex switch
        {
            RpcException rpc when rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal => true,
            IOException => true,
            SocketException => true,
            InvalidOperationException ioe when ioe.Message?.Contains("HTTP/2", StringComparison.OrdinalIgnoreCase) == true &&
                                               ioe.Message?.Contains("not established", StringComparison.OrdinalIgnoreCase) == true => true,
            ObjectDisposedException => true,
            _ => false
        };

    private DateTime? ComputeDeadline(TimeSpan? overrideTimeout = null)
    {
        var effective = overrideTimeout ?? _defaultTimeout;
        return effective.HasValue ? DateTime.UtcNow + effective.Value : (DateTime?)null;
    }

    private async Task<string> EnsureServerRunning(
        RepoDirectoryAccessor repoDirectory,
        string initialSocketPath,
        bool allowReResolve,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "RepoQlClient: ensure server running (repoRoot='{RepoRoot}', initialSocket='{Socket}', allowReResolve={Allow}, timeoutMs={Timeout}).",
            repoDirectory.RepoRoot,
            initialSocketPath,
            allowReResolve,
            timeout.TotalMilliseconds);

        if (await TryHealthCheckAsync(initialSocketPath, cancellationToken))
        {
            _logger.LogInformation("RepoQlClient: existing host healthy on '{Socket}'.", initialSocketPath);
            return initialSocketPath;
        }

        _logger.LogInformation("RepoQlClient: launching host for repoRoot='{RepoRoot}'.", repoDirectory.RepoRoot);
        LaunchHost(repoDirectory.RepoRoot, _logger);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var currentSocketPath = initialSocketPath;
        while (sw.Elapsed < timeout)
        {
            if (allowReResolve)
            {
                try { currentSocketPath = repoDirectory.ResolveSocketPath(); } catch { }
            }

            if (await TryHealthCheckAsync(currentSocketPath, cancellationToken))
            {
                _logger.LogInformation(
                    "RepoQlClient: host healthy on '{Socket}' after {Elapsed} ms.",
                    currentSocketPath,
                    sw.Elapsed.TotalMilliseconds);
                return currentSocketPath;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        // Include diagnostics from the host process if available
        var (stderr, exitCode) = GetLastHostDiagnostics();
        var diagnosticInfo = "";
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            diagnosticInfo = $" Host exited with code {exitCode.Value}.";
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var stderrPreview = stderr.Length > 500 ? stderr[..500] + "..." : stderr;
                diagnosticInfo += $" Stderr: {stderrPreview.Trim()}";
            }
        }

        _logger.LogError(
            "RepoQlClient: host did not become ready within {Timeout} ms (lastSocket='{Socket}').{Diagnostics}",
            timeout.TotalMilliseconds,
            currentSocketPath,
            diagnosticInfo);
        throw new TimeoutException($"RepoQL host did not become ready within {timeout.TotalMilliseconds} ms (socket: {currentSocketPath}).{diagnosticInfo}");
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

    private static async Task<bool> TryHealthCheckAsync(string socketPath, CancellationToken cancellationToken)
    {
        var socket = await ConnectAsync(socketPath, cancellationToken).ConfigureAwait(false);
        if (socket is null)
            return false;

        try
        {
            return HealthServing(socket);
        }
        finally
        {
            try { socket.Dispose(); } catch { }
        }
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

    private static void LaunchHost(string repoPath, ILogger logger)
    {
        var implicitEnv = new Dictionary<string, string?>
        {
            ["REPOQL_IMPLICIT"] = "1",
        };

        foreach (var env in Environment.GetEnvironmentVariables().OfType<DictionaryEntry>().Where(kvp =>
                         kvp.Key is string key && key.StartsWith("REPOQL_", StringComparison.OrdinalIgnoreCase))
                     .Select(kvp => new
                     {
                         Key = (string)kvp.Key,
                         Value = kvp.Value?.ToString()
                     }))
        {
            implicitEnv.Add(env.Key, env.Value);
        }

        var args = new List<string> { "serve", "--implicit-start" };
        var currentExe = Environment.ProcessPath;
        string launchTarget;
        if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
        {
            launchTarget = currentExe!;
        }
        else
        {
            var baseDir = AppContext.BaseDirectory;
            launchTarget = BuildExecutableCandidates(Path.Join(baseDir, "repoql")).FirstOrDefault(File.Exists) ?? "repoql";
        }

        logger.LogInformation("RepoQlClient: launching host '{Exe}' for repo '{RepoPath}' with args '{Args}'.",
            launchTarget,
            repoPath,
            string.Join(' ', args));
        StartProcess(launchTarget, repoPath, args, implicitEnv);
    }

    private static IEnumerable<string> BuildExecutableCandidates(string basePath)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return basePath + ".exe";
            yield return basePath + ".dll"; // dotnet <dll>
        }
        else
        {
            // On non-Windows, prefer native self-contained binary over .dll
            // to avoid requiring dotnet on PATH
            yield return basePath;           // self-contained
            yield return basePath + ".dll"; // dotnet <dll>
        }
    }

    private static string? _lastHostStderr;
    private static int? _lastHostExitCode;
    private static readonly object _hostDiagnosticsLock = new();

    /// <summary>
    /// Gets diagnostic information from the last host launch attempt.
    /// Useful for debugging startup failures.
    /// </summary>
    public static (string? Stderr, int? ExitCode) GetLastHostDiagnostics()
    {
        lock (_hostDiagnosticsLock)
        {
            return (_lastHostStderr, _lastHostExitCode);
        }
    }

    private static void StartProcess(string exePathOrCommand, string workingDirectory, IEnumerable<string> args, IDictionary<string, string?> env)
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
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError = true
        };
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch RepoQL host using '{exePathOrCommand}'. Set REPOQL_HOST_PATH to override.", ex);
        }

        // Capture stderr asynchronously for diagnostics
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                process.WaitForExit(30000); // Wait up to 30s for exit code

                lock (_hostDiagnosticsLock)
                {
                    _lastHostStderr = stderr;
                    _lastHostExitCode = process.HasExited ? process.ExitCode : null;
                }

                // If process exited quickly with an error, log it
                if (process.HasExited && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                {
                    Console.Error.WriteLine($"[RepoQlClient] Host process exited with code {process.ExitCode}. Stderr: {stderr.Trim()}");
                }
            }
            catch
            {
                // Ignore errors in diagnostic capture
            }
        });
    }

    private static int EnvironmentTimeout(string name, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    private void EstablishLeaseOrThrow(string repoPath, TimeSpan timeout)
    {
        if (_leaseCts is null)
            throw new InvalidOperationException("Lease token source is not initialized.");
        var channel = _channel ?? throw new InvalidOperationException("RepoQL client channel is not established.");
        _logger.LogInformation("RepoQlClient: establishing lease for repo '{RepoPath}' (timeoutMs={Timeout}).",
            repoPath,
            timeout.TotalMilliseconds);
        var leaseClient = new Contracts.RepoQL.RepoQLClient(channel);
        var leaseCall = leaseClient.HoldClientLease(cancellationToken: _leaseCts.Token);
        _leaseCall = leaseCall;

        var clientId = Guid.NewGuid().ToString();
        var pid = Environment.ProcessId;
        var tool = AppDomain.CurrentDomain.FriendlyName;
        var ver = typeof(RepoQlClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var startedAt = DateTime.UtcNow.ToString("O");

        // Send first beat and ensure it succeeds within timeout
        var firstBeat = leaseCall.RequestStream.WriteAsync(new ClientLeaseBeat
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
                await leaseCall.RequestStream.WriteAsync(new ClientLeaseBeat
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
    public Task<RawQueryResponse> ExecuteRawQueryAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            var req = BuildRawQueryRequest(sql, parameters, rowLimit);
            var deadline = ComputeDeadline();
            return await client.ExecuteRawQueryAsync(req, deadline: deadline, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<RawQueryRow> ExecuteRawQueryStreamAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            await EnsureConnectedAsync(forceReconnect: attempt > 0, cancellationToken).ConfigureAwait(false);
            var client = _client ?? throw new InvalidOperationException("RepoQL client is not connected.");
            var req = BuildRawQueryRequest(sql, parameters, rowLimit);
            var deadline = ComputeDeadline();
            using var call = client.ExecuteRawQueryStream(req, deadline: deadline, cancellationToken: cancellationToken);
            var emitted = false;
            Exception? failure = null;

            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    yield break;
                }

                emitted = true;
                yield return call.ResponseStream.Current;
            }

            if (failure != null && !emitted && attempt == 0 && _mode == ConnectionMode.Managed && ShouldAttemptReconnect(failure))
            {
                DisposeChannel();
                attempt++;
                continue;
            }

            throw failure ?? new InvalidOperationException("RepoQL stream failed unexpectedly.");
        }
    }

    /// <inheritdoc />
    public Task<GetDocumentSummariesResponse> GetDocumentSummariesAsync(
        IEnumerable<string> documentUris,
        IEnumerable<string>? annotationKinds = null,
        string? minimumSeverity = null,
        bool includeData = false,
        bool includeMessage = true,
        bool includeResolvedTargetUri = false,
        CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            var req = BuildSummariesRequest(documentUris, annotationKinds, minimumSeverity, includeData, includeMessage, includeResolvedTargetUri);
            var deadline = ComputeDeadline();
            return await client.GetDocumentSummariesAsync(req, deadline: deadline, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        DisposeChannel();
        _repoDirectory?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static RawQueryRequest BuildRawQueryRequest(string sql, IEnumerable<object?>? parameters, int? rowLimit)
    {
        var req = new RawQueryRequest
        {
            Sql = sql,
            Limit = rowLimit.GetValueOrDefault(0)
        };
        foreach (var p in parameters ?? [])
            req.Parameters.Add(ToValue(p));
        return req;
    }

    private static GetDocumentSummariesRequest BuildSummariesRequest(
        IEnumerable<string> documentUris,
        IEnumerable<string>? annotationKinds,
        string? minimumSeverity,
        bool includeData,
        bool includeMessage,
        bool includeResolvedTargetUri)
    {
        var req = new GetDocumentSummariesRequest
        {
            MinSeverity = minimumSeverity ?? string.Empty,
            IncludeData = includeData,
            IncludeMessage = includeMessage,
            IncludeResolvedTargetUri = includeResolvedTargetUri
        };

        foreach (var u in documentUris)
            if (!string.IsNullOrWhiteSpace(u))
                req.Uris.Add(u);

        if (annotationKinds != null)
        {
            foreach (var k in annotationKinds)
                if (!string.IsNullOrWhiteSpace(k))
                    req.Kinds.Add(k);
        }

        return req;
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


    public async IAsyncEnumerable<ReindexProgress> ReindexAllAsync(bool clear = false, TimeSpan? timeout = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            await EnsureConnectedAsync(forceReconnect: attempt > 0, cancellationToken).ConfigureAwait(false);
            var client = _client ?? throw new InvalidOperationException("RepoQL client is not connected.");
            var deadline = ComputeDeadline(timeout);
            using var call = client.ReindexAll(new ReindexRequest { Clear = clear }, deadline: deadline, cancellationToken: cancellationToken);
            var emitted = false;
            Exception? failure = null;

            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    yield break;
                }

                emitted = true;
                yield return call.ResponseStream.Current;
            }

            if (failure != null && !emitted && attempt == 0 && _mode == ConnectionMode.Managed && ShouldAttemptReconnect(failure))
            {
                DisposeChannel();
                attempt++;
                continue;
            }

            throw failure ?? new InvalidOperationException("RepoQL stream failed unexpectedly.");
        }
    }

    public Task<ProtoPipelineStatus> WaitForPipelineAsync(
        IEnumerable<ProtoPipelineStage>? stages = null,
        bool waitAll = true,
        CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            var request = new WaitForPipelineRequest { WaitAll = waitAll };
            if (stages is not null)
                request.Stages.AddRange(stages);

            var response = await client.WaitForPipelineAsync(request, deadline: ComputeDeadline(), cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
            return response.Status ?? new ProtoPipelineStatus();
        }, cancellationToken);

    public Task<ProtoPipelineStatus> ImportRepositoryAsync(
        string uri,
        ProtoPipelineStage? waitStage = null,
        CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("uri is required", nameof(uri));

            var request = new ImportRequest
            {
                Uri = uri.Trim()
            };

            if (waitStage.HasValue)
                request.WaitStage = waitStage.Value;

            var response = await client.ImportRepositoryAsync(request, deadline: ComputeDeadline(), cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
            return response.Status ?? new ProtoPipelineStatus();
        }, cancellationToken);

    public Task<ProtoPipelineStatus> GetPipelineStatusAsync(CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            var response = await client.GetPipelineStatusAsync(new GetPipelineStatusRequest(), deadline: ComputeDeadline(), cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
            return response.Status ?? new ProtoPipelineStatus();
        }, cancellationToken);

    public Task<PreviewDocumentResponse> PreviewDocumentAsync(
        string uri,
        byte[]? content = null,
        string? fileName = null,
        string? mediaTypeHint = null,
        CancellationToken cancellationToken = default)
        => InvokeWithReconnectAsync(async (client, ct) =>
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("uri is required", nameof(uri));

            var request = new PreviewDocumentRequest
            {
                Uri = uri
            };

            if (!string.IsNullOrWhiteSpace(fileName))
                request.FileName = fileName;
            if (!string.IsNullOrWhiteSpace(mediaTypeHint))
                request.MediaTypeHint = mediaTypeHint;
            if (content is { Length: > 0 })
                request.Content = ByteString.CopyFrom(content);

            var response = await client.PreviewDocumentAsync(request, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
            return response;
        }, cancellationToken);
}
