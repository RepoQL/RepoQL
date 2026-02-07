using System;
using System.Threading;
using System.Text.Json;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessToolRouterTests
{
    [Test]
    public async Task TryHandleToolCall_HandlesHarnessStatus()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero));
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", new DateTimeOffset(2026, 2, 5, 14, 30, 5, TimeSpan.Zero));
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            new StubLifecycleOperations(),
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"harness.status","arguments":{}}}""";

        var responseTask = router.TryHandleToolCallAsync(request, 12345, CancellationToken.None);

        responseTask.Should().NotBeNull();
        var response = await responseTask!;

        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("_harness").GetProperty("request_id").GetString().Should().NotBeNullOrEmpty();

        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        payloadText.Should().NotBeNullOrEmpty();

        using var payloadDoc = JsonDocument.Parse(payloadText!);
        var harness = payloadDoc.RootElement.GetProperty("harness");
        harness.GetProperty("session_id").GetString().Should().Be("sess_20260205143022_abcd");
        harness.GetProperty("started_at").GetString().Should().Be("2026-02-05T14:30:00Z");
        harness.GetProperty("subprocess_pid").GetInt32().Should().Be(12345);
        harness.GetProperty("aspire_connected").GetBoolean().Should().BeTrue();
        var host = payloadDoc.RootElement.GetProperty("host");
        host.GetProperty("state").GetString().Should().Be("ready");
        host.GetProperty("resource_name").GetString().Should().Be("host");
        payloadDoc.RootElement.GetProperty("current_operation").ValueKind.Should().Be(JsonValueKind.Null);
        payloadDoc.RootElement.GetProperty("coordination_lock").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task TryHandleToolCall_SkipsNonHarnessTools()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            new StubLifecycleOperations(),
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"query","arguments":{}}}""";

        var responseTask = router.TryHandleToolCallAsync(request, 12345, CancellationToken.None);

        responseTask.Should().BeNull();
    }

    [Test]
    public async Task TryHandleToolCall_HandlesHarnessBuild()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var lifecycle = new StubLifecycleOperations
        {
            BuildResult = HarnessLifecycleResult.Succeeded("Build ok.")
        };
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            lifecycle,
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"harness.build","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        lifecycle.BuildCalls.Should().Be(1);
        coordinator.AcquireCalls.Should().Be(1);
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payloadDoc.RootElement.GetProperty("message").GetString().Should().Be("Build ok.");
    }

    [Test]
    public async Task TryHandleToolCall_IncludesBuildOutputOnFailure()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var lifecycle = new StubLifecycleOperations
        {
            BuildResult = HarnessLifecycleResult.Failed("Build failed.", "error CS1002: ; expected\nerror CS0246: type not found")
        };
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            lifecycle,
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"harness.build","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        payloadDoc.RootElement.GetProperty("error").GetString().Should().Be("Build failed.");
        payloadDoc.RootElement.GetProperty("output").GetString().Should().Contain("CS1002");
        payloadDoc.RootElement.GetProperty("output").GetString().Should().Contain("CS0246");
    }

    [Test]
    public async Task TryHandleToolCall_HandlesHarnessRestart()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var lifecycle = new StubLifecycleOperations
        {
            RestartResult = HarnessLifecycleResult.Succeeded("Restart ok.")
        };
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            lifecycle,
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"harness.restart","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        lifecycle.RestartCalls.Should().Be(1);
        coordinator.AcquireCalls.Should().Be(1);
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payloadDoc.RootElement.GetProperty("message").GetString().Should().Be("Restart ok.");
    }

    [Test]
    public async Task TryHandleToolCall_ReturnsOperationInProgress()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var lifecycle = new StubLifecycleOperations();
        var operationState = new HarnessOperationState();
        operationState.TryBegin(HarnessOperationKind.Building, new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero));
        var coordinator = new StubOperationCoordinator();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            lifecycle,
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"harness.build","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        lifecycle.BuildCalls.Should().Be(0);
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("error").GetString().Should().Be("operation_in_progress");
        payloadDoc.RootElement.GetProperty("operation").GetString().Should().Be("building");
    }

    [Test]
    public async Task TryHandleToolCall_HandlesWaitForOperation()
    {
        var session = new HarnessSessionInfo("sess_20260205143022_abcd", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var operationState = new HarnessOperationState();
        var coordinator = new StubOperationCoordinator
        {
            WaitReleased = true
        };
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            operationState,
            coordinator,
            new StubLifecycleOperations(),
            new StubTelemetryClient());
        var request = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"harness.wait_for_operation","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        coordinator.WaitCalls.Should().Be(1);
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("message").GetString().Should().Be("Operation completed.");
    }

    [Test]
    public async Task TryHandleToolCall_HandlesConsoleLogsWithDefaults()
    {
        var session = new HarnessSessionInfo("sess_test", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var telemetry = new StubTelemetryClient();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            new HarnessOperationState(),
            new StubOperationCoordinator(),
            new StubLifecycleOperations(),
            telemetry);
        var request = """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"harness.console_logs","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        telemetry.LastConsoleLogsResource.Should().Be("host");
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(3);
        payloadDoc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Test]
    public async Task TryHandleToolCall_HandlesConsoleLogsWithCustomResource()
    {
        var session = new HarnessSessionInfo("sess_test", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var telemetry = new StubTelemetryClient();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            new HarnessOperationState(),
            new StubOperationCoordinator(),
            new StubLifecycleOperations(),
            telemetry);
        var request = """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"harness.console_logs","arguments":{"resource":"web","limit":2}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        telemetry.LastConsoleLogsResource.Should().Be("web");
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        // Limit 2 = last 2 lines: "line2", "line3"
        payloadDoc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(2);
        payloadDoc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task TryHandleToolCall_HandlesTraceLogs()
    {
        var session = new HarnessSessionInfo("sess_test", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var telemetry = new StubTelemetryClient();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            new HarnessOperationState(),
            new StubOperationCoordinator(),
            new StubLifecycleOperations(),
            telemetry);
        var request = """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"harness.trace_logs","arguments":{"trace_id":"abc123def456"}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        telemetry.LastTraceLogsTraceId.Should().Be("abc123def456");
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task TryHandleToolCall_TraceLogsRequiresTraceId()
    {
        var session = new HarnessSessionInfo("sess_test", DateTimeOffset.UtcNow);
        var snapshot = new HostStateSnapshot(HostState.Ready, true, "host", DateTimeOffset.UtcNow);
        var telemetry = new StubTelemetryClient();
        var router = new HarnessToolRouter(
            session,
            new StaticHostStateProvider(snapshot),
            new HarnessOperationState(),
            new StubOperationCoordinator(),
            new StubLifecycleOperations(),
            telemetry);
        var request = """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"harness.trace_logs","arguments":{}}}""";

        var response = await router.TryHandleToolCallAsync(request, 12345, CancellationToken.None)!;

        telemetry.LastTraceLogsTraceId.Should().BeNull();
        using var responseDoc = JsonDocument.Parse(response);
        var result = responseDoc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var payloadText = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var payloadDoc = JsonDocument.Parse(payloadText!);
        payloadDoc.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        payloadDoc.RootElement.GetProperty("message").GetString().Should().Contain("trace_id");
    }

    private sealed class StaticHostStateProvider : IHostStateProvider
    {
        private readonly HostStateSnapshot _snapshot;

        public StaticHostStateProvider(HostStateSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public HostStateSnapshot GetSnapshot() => _snapshot;
    }

    private sealed class StubLifecycleOperations : IHarnessLifecycleOperations
    {
        public int BuildCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public int DeployCalls { get; private set; }
        public HarnessLifecycleResult BuildResult { get; set; } = HarnessLifecycleResult.Succeeded("Build succeeded.");
        public HarnessLifecycleResult RestartResult { get; set; } = HarnessLifecycleResult.Succeeded("Restart succeeded.");
        public HarnessLifecycleResult DeployResult { get; set; } = HarnessLifecycleResult.Succeeded("Deploy succeeded.");

        public Task<HarnessLifecycleResult> BuildAsync(CancellationToken cancellationToken)
        {
            BuildCalls++;
            return Task.FromResult(BuildResult);
        }

        public Task<HarnessLifecycleResult> RestartAsync(CancellationToken cancellationToken)
        {
            RestartCalls++;
            return Task.FromResult(RestartResult);
        }

        public Task<HarnessLifecycleResult> DeployAsync(CancellationToken cancellationToken)
        {
            DeployCalls++;
            return Task.FromResult(DeployResult);
        }
    }

    private sealed class StubOperationCoordinator : IHarnessOperationCoordinator
    {
        public int AcquireCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public string? LockDescription { get; set; }
        public (IDisposable? Handle, string? Error) AcquireResult { get; set; } = (new StubLockHandle(), null);
        public bool WaitReleased { get; set; } = true;

        public string? GetActiveLockDescription() => LockDescription;

        public (IDisposable? Handle, string? Error) TryAcquire(string sessionId, string operation, DateTimeOffset startedAt)
        {
            AcquireCalls++;
            return AcquireResult;
        }

        public Task<bool> WaitForReleaseAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.FromResult(WaitReleased);
        }
    }

    private sealed class StubLockHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class StubTelemetryClient : IAspireTelemetryClient
    {
        public string? ConsoleLogsResult { get; set; } = "line1\nline2\nline3";
        public string? TraceLogsResult { get; set; } = "[]";
        public string? LastConsoleLogsResource { get; set; }
        public string? LastTraceLogsTraceId { get; set; }

        public Task<AspireTelemetryResult> ListStructuredLogsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
            => Task.FromResult(AspireTelemetryResult.Ok("[]"));

        public Task<AspireTelemetryResult> ListTracesAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
            => Task.FromResult(AspireTelemetryResult.Ok("[]"));

        public Task<AspireTelemetryResult> ListConsoleLogsAsync(string resourceName, CancellationToken cancellationToken)
        {
            LastConsoleLogsResource = resourceName;
            return Task.FromResult(AspireTelemetryResult.Ok(ConsoleLogsResult));
        }

        public Task<AspireTelemetryResult> ListTraceStructuredLogsAsync(string traceId, CancellationToken cancellationToken)
        {
            LastTraceLogsTraceId = traceId;
            return Task.FromResult(AspireTelemetryResult.Ok(TraceLogsResult));
        }

        public Task<AspireCommandResult> ExecuteResourceCommandAsync(string resourceName, string commandName, CancellationToken cancellationToken)
            => Task.FromResult(AspireCommandResult.Fail("Not configured for command execution."));
    }
}
