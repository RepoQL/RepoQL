using AwesomeAssertions;
using FakeItEasy;
using Grpc.Core;
using RepoQL.Contracts.Cloud;
using RepoQL.Embedding.Client;

namespace RepoQL.Embedding.Client.Tests;

public sealed class GrpcEmbeddingProviderTests
{
    private static readonly ICloudCredentialProvider StaticProvider = new StubCloudCredentialProvider("secret-token");

    [Test]
    public async Task EmbedQueryAsync_AddsAuthorizationHeader()
    {
        var grpcClient = A.Fake<EmbeddingService.EmbeddingServiceClient>();
        Metadata? capturedHeaders = null;

        A.CallTo(() => grpcClient.GetModelInfoAsync(
                A<GetModelInfoRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Invokes(call => capturedHeaders = call.GetArgument<Metadata?>(1))
            .Returns(CreateUnaryCall(new GetModelInfoResponse
            {
                Model = "voyage",
                Dimension = 1024
            }));

        A.CallTo(() => grpcClient.EmbedQueryAsync(
                A<EmbedQueryRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Invokes(call => capturedHeaders = call.GetArgument<Metadata?>(1))
            .Returns(CreateUnaryCall(new EmbedQueryResponse
            {
                Vector = { 1.0f, 2.0f }
            }));

        using var provider = new GrpcEmbeddingProvider(grpcClient, StaticProvider);

        var vector = await provider.EmbedQueryAsync("hello");

        vector.Should().Equal([1.0f, 2.0f]);
        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Single(h => h.Key == "authorization").Value.Should().Be("Bearer secret-token");
    }

    [Test]
    public async Task InitializeAsync_LoadsModelInfo()
    {
        var grpcClient = A.Fake<EmbeddingService.EmbeddingServiceClient>();

        A.CallTo(() => grpcClient.GetModelInfoAsync(
                A<GetModelInfoRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateUnaryCall(new GetModelInfoResponse
            {
                Model = "voyage-code-3",
                Dimension = 2048
            }));

        using var provider = new GrpcEmbeddingProvider(grpcClient, StaticProvider);

        await provider.InitializeAsync();

        provider.Model.Should().Be("voyage-code-3");
        provider.Dimension.Should().Be(2048);
    }

    [Test]
    public async Task InitializeAsync_RetriesOnceOnUnauthenticatedWithRefreshedToken()
    {
        var grpcClient = A.Fake<EmbeddingService.EmbeddingServiceClient>();
        var credentialProvider = new RefreshingCloudCredentialProvider("expired-token", "fresh-token");
        var headers = new List<string?>();
        var calls = 0;

        A.CallTo(() => grpcClient.GetModelInfoAsync(
                A<GetModelInfoRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .ReturnsLazily(call =>
            {
                calls++;
                headers.Add(call.GetArgument<Metadata?>(1)?.SingleOrDefault(h => h.Key == "authorization").Value);
                return calls == 1
                    ? CreateFaultedUnaryCall<GetModelInfoResponse>(
                        new RpcException(new Status(StatusCode.Unauthenticated, "expired")))
                    : CreateUnaryCall(new GetModelInfoResponse
                    {
                        Model = "voyage-code-3",
                        Dimension = 2048
                    });
            });

        using var provider = new GrpcEmbeddingProvider(grpcClient, credentialProvider);

        await provider.InitializeAsync();

        provider.Model.Should().Be("voyage-code-3");
        provider.Dimension.Should().Be(2048);
        headers.Should().Equal(["Bearer expired-token", "Bearer fresh-token"]);
        credentialProvider.RefreshCallCount.Should().Be(1);
    }

    private static AsyncUnaryCall<T> CreateUnaryCall<T>(T response)
        where T : class
        => new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private static AsyncUnaryCall<T> CreateFaultedUnaryCall<T>(RpcException exception)
        where T : class
        => new(
            Task.FromException<T>(exception),
            Task.FromResult(new Metadata()),
            () => exception.Status,
            () => new Metadata(),
            () => { });

    private sealed class StubCloudCredentialProvider(string token) : ICloudCredentialProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);

        public Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }

    private sealed class RefreshingCloudCredentialProvider(string initialToken, string refreshedToken) : ICloudCredentialProvider
    {
        public int RefreshCallCount { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(initialToken);

        public Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            RefreshCallCount++;
            return Task.FromResult(refreshedToken);
        }
    }
}
