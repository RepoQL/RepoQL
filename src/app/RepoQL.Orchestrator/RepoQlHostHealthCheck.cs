using System.Net.Sockets;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.Orchestrator;

internal sealed class RepoQlHostHealthCheck : IHealthCheck
{
    private const string HealthServiceName = "repoql.v1.RepoQL";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var repoRoot = RepoLocator.FindRepoRoot();
            var socketPath = RepoqlSocketPathResolver.ResolvePhysical(repoRoot);

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            using var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
            {
                HttpHandler = handler,
                Credentials = ChannelCredentials.Insecure
            });

            var client = new Health.HealthClient(channel);
            var response = await client.CheckAsync(new HealthCheckRequest { Service = HealthServiceName }, cancellationToken: cancellationToken).ConfigureAwait(false);

            return response.Status == HealthCheckResponse.Types.ServingStatus.Serving
                ? HealthCheckResult.Healthy("RepoQL host is serving requests.")
                : HealthCheckResult.Unhealthy($"RepoQL host reported status '{response.Status}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }

}
