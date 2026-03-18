using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RepoQL.Testing;

/// <summary>
/// Purpose: Enable TUnit lifecycle tracing across all test assemblies when an exporter is configured.
/// Complexity: Shared test infrastructure keeps OpenTelemetry setup in one place and avoids repeating
/// the same TUnit session hooks in every test project.
/// </summary>
internal static class TUnitOpenTelemetrySetup
{
    private const string ConsoleExporterEnvVar = "REPOQL_TESTS_OTEL_CONSOLE";
    private const string OtlpEndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private static TracerProvider? tracerProvider;

    [Before(TestSession)]
    public static void SetupTracing()
    {
        if (!TryConfigureExporter(out var configureExporter))
        {
            return;
        }

        var assemblyName = typeof(TUnitOpenTelemetrySetup).Assembly.GetName().Name ?? "RepoQL.Tests";
        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(assemblyName))
            .AddSource("TUnit");

        configureExporter(builder);
        tracerProvider = builder.Build();
    }

    [After(TestSession)]
    public static void TeardownTracing()
    {
        tracerProvider?.Dispose();
        tracerProvider = null;
    }

    private static bool TryConfigureExporter(out Action<TracerProviderBuilder> configureExporter)
    {
        var otlpEndpoint = Environment.GetEnvironmentVariable(OtlpEndpointEnvVar);
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            configureExporter = builder => builder.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
            return true;
        }

        if (IsTruthy(Environment.GetEnvironmentVariable(ConsoleExporterEnvVar)))
        {
            configureExporter = builder => builder.AddConsoleExporter();
            return true;
        }

        configureExporter = static _ => { };
        return false;
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
