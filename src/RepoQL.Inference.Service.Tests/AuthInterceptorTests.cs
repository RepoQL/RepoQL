using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RepoQL.Cloud.Auth;

namespace RepoQL.Inference.Service.Tests;

public sealed class AuthInterceptorTests
{
    [Test]
    public async Task UnaryServerHandler_AcceptsLegacyApiKeyAndSetsAuthIdentity()
    {
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                ApiKeyHashes = [ComputeHash("rql_expected-token")]
            });
        var context = new TestServerCallContext("Bearer rql_expected-token");

        var response = await interceptor.UnaryServerHandler(
            "request",
            context,
            static (_, serverContext) => Task.FromResult(serverContext.RequireAuthIdentity().DisplayName));

        await Assert.That(response).IsEqualTo(ComputeHash("rql_expected-token")[..8]);
        context.RequireAuthIdentity().UserId.Should().Be("api-key");
        context.RequireAuthIdentity().Method.Should().Be(AuthMethod.ApiKey);
    }

    [Test]
    public async Task UnaryServerHandler_RejectsUnrecognizedTokenFormat()
    {
        var interceptor = CreateInterceptor(new AuthOptions
        {
            ApiKeyHashes = [ComputeHash("rql_expected-token")]
        });
        var context = new TestServerCallContext("Bearer definitely-not-a-jwt");

        var exception = await Assert.That(async () => await interceptor.UnaryServerHandler(
                "request",
                context,
                static (_, _) => Task.FromResult("ok")))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(exception.Status.Detail).IsEqualTo("Unrecognized token format. Run: repoql login");
    }

    [Test]
    public async Task UnaryServerHandler_RejectsExpiredSessionToken()
    {
        using var keyMaterial = RsaKeyMaterial.Create("kid-expired");
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                JwksUri = "https://auth.example.test/oauth2/jwks",
                ClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57"
            },
            keyMaterial.CreateJwksResponse());
        var context = new TestServerCallContext($"Bearer {CreateSessionToken(keyMaterial, expiresUtc: DateTime.UtcNow.AddMinutes(-10))}");

        var exception = await Assert.That(async () => await interceptor.UnaryServerHandler(
                "request",
                context,
                static (_, _) => Task.FromResult("ok")))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(exception.Status.Detail).IsEqualTo("Session expired. Run: repoql login");
    }

    [Test]
    public async Task ServerStreamingServerHandler_AcceptsValidSessionTokenAndSetsAuthIdentity()
    {
        using var keyMaterial = RsaKeyMaterial.Create("kid-valid");
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                JwksUri = "https://auth.example.test/oauth2/jwks",
                ClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57"
            },
            keyMaterial.CreateJwksResponse());
        var token = CreateSessionToken(
            keyMaterial,
            expiresUtc: DateTime.UtcNow.AddMinutes(30),
            orgId: "org_123");
        var context = new TestServerCallContext($"Bearer {token}");
        var writer = new TestServerStreamWriter<string>();
        AuthIdentity? capturedIdentity = null;

        await interceptor.ServerStreamingServerHandler(
            "request",
            writer,
            context,
            (_, _, serverContext) =>
            {
                capturedIdentity = serverContext.RequireAuthIdentity();
                return Task.CompletedTask;
            });

        capturedIdentity.Should().NotBeNull();
        capturedIdentity!.UserId.Should().Be("user_123");
        capturedIdentity.OrganizationId.Should().Be("org_123");
        capturedIdentity.Method.Should().Be(AuthMethod.Session);
        capturedIdentity.DisplayName.Should().Be("user_123");
    }

    [Test]
    public async Task ClientStreamingServerHandler_AcceptsValidSessionTokenAndSetsAuthIdentity()
    {
        using var keyMaterial = RsaKeyMaterial.Create("kid-client-stream");
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                JwksUri = "https://auth.example.test/oauth2/jwks",
                ClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57",
                Issuer = "https://issuer.example.test"
            },
            keyMaterial.CreateJwksResponse());
        var token = CreateSessionToken(keyMaterial, expiresUtc: DateTime.UtcNow.AddMinutes(30));
        var context = new TestServerCallContext($"Bearer {token}");

        var response = await interceptor.ClientStreamingServerHandler(
            new TestAsyncStreamReader<string>(),
            context,
            static (_, serverContext) => Task.FromResult(serverContext.RequireAuthIdentity().UserId));

        await Assert.That(response).IsEqualTo("user_123");
        context.RequireAuthIdentity().Method.Should().Be(AuthMethod.Session);
    }

    [Test]
    public async Task DuplexStreamingServerHandler_RejectsInvalidSessionSignature()
    {
        using var jwksKey = RsaKeyMaterial.Create("kid-jwks");
        using var signingKey = RsaKeyMaterial.Create("kid-signer");
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                JwksUri = "https://auth.example.test/oauth2/jwks",
                ClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57"
            },
            jwksKey.CreateJwksResponse());
        var token = CreateSessionToken(signingKey, expiresUtc: DateTime.UtcNow.AddMinutes(30));
        var context = new TestServerCallContext($"Bearer {token}");

        var exception = await Assert.That(async () => await interceptor.DuplexStreamingServerHandler(
                new TestAsyncStreamReader<string>(),
                new TestServerStreamWriter<string>(),
                context,
                static (_, _, _) => Task.CompletedTask))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(exception.Status.Detail).IsEqualTo("Invalid session. Run: repoql login");
    }

    [Test]
    public async Task UnaryServerHandler_RejectsUnexpectedIssuer_WhenIssuerConfigured()
    {
        using var keyMaterial = RsaKeyMaterial.Create("kid-issuer");
        var interceptor = CreateInterceptor(
            new AuthOptions
            {
                JwksUri = "https://auth.example.test/oauth2/jwks",
                ClientId = "client_01KKDYHD5DF3E3SHDPPN3BMB57",
                Issuer = "https://expected-issuer.example.test"
            },
            keyMaterial.CreateJwksResponse());
        var context = new TestServerCallContext($"Bearer {CreateSessionToken(keyMaterial, expiresUtc: DateTime.UtcNow.AddMinutes(30))}");

        var exception = await Assert.That(async () => await interceptor.UnaryServerHandler(
                "request",
                context,
                static (_, _) => Task.FromResult("ok")))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(exception.Status.Detail).IsEqualTo("Invalid session. Run: repoql login");
    }

#if DEBUG
    [Test]
    public async Task UnaryServerHandler_BypassesAuthWhenDebugAuthIsUnconfigured()
    {
        var interceptor = CreateInterceptor(new AuthOptions());
        var context = new TestServerCallContext(null);

        var response = await interceptor.UnaryServerHandler(
            "request",
            context,
            static (_, serverContext) => Task.FromResult(serverContext.RequireAuthIdentity().UserId));

        await Assert.That(response).IsEqualTo("debug-auth-bypass");
        context.RequireAuthIdentity().DisplayName.Should().Be("debug-auth-bypass");
    }
#endif

    private static AuthInterceptor CreateInterceptor(AuthOptions options, string? jwksDocument = null)
    {
        var httpClientFactory = new StubHttpClientFactory(jwksDocument);
        var validationService = new AuthValidationService(
            Options.Create(options),
            httpClientFactory,
            NullLogger<AuthValidationService>.Instance);

        return new AuthInterceptor(validationService);
    }

    private static string CreateSessionToken(
        RsaKeyMaterial keyMaterial,
        DateTime expiresUtc,
        string? orgId = null)
    {
        var handler = new JsonWebTokenHandler();
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "user_123",
            ["sid"] = "session_123",
            ["jti"] = Guid.NewGuid().ToString("N")
        };

        if (!string.IsNullOrWhiteSpace(orgId))
            claims["org_id"] = orgId;

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Audience = "client_01KKDYHD5DF3E3SHDPPN3BMB57",
            Claims = claims,
            Expires = expiresUtc,
            IssuedAt = expiresUtc.AddMinutes(-5),
            Issuer = "https://issuer.example.test",
            SigningCredentials = new SigningCredentials(keyMaterial.SecurityKey, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "user_123"),
                new Claim("sid", "session_123"),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ])
        });
    }

    private static string ComputeHash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string? _jwksDocument;

        public StubHttpClientFactory(string? jwksDocument)
        {
            _jwksDocument = jwksDocument;
        }

        public HttpClient CreateClient(string name)
        {
            var handler = new StubHttpMessageHandler(_jwksDocument);
            return new HttpClient(handler)
            {
                BaseAddress = new Uri("https://auth.example.test")
            };
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _jwksDocument;

        public StubHttpMessageHandler(string? jwksDocument)
        {
            _jwksDocument = jwksDocument;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_jwksDocument))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jwksDocument),
                RequestMessage = request
            });
        }
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly string? _authorizationHeader;
        private readonly Metadata _requestHeaders;
        private readonly Metadata _responseTrailers = [];
        private readonly Dictionary<object, object> _userState = [];
        private Status _status;
        private WriteOptions? _writeOptions;

        public TestServerCallContext(string? authorizationHeader)
        {
            _authorizationHeader = authorizationHeader;
            _requestHeaders = [];

            if (!string.IsNullOrWhiteSpace(_authorizationHeader))
                _requestHeaders.Add("authorization", _authorizationHeader);
        }

        protected override string MethodCore => "test";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "peer";

        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);

        protected override Metadata RequestHeadersCore => _requestHeaders;

        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        protected override Metadata ResponseTrailersCore => _responseTrailers;

        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override IDictionary<object, object> UserStateCore => _userState;
    }

    private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public List<T> Writes { get; } = [];

        public Task WriteAsync(T message)
        {
            Writes.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RsaKeyMaterial : IDisposable
    {
        private readonly RSA _rsa;

        private RsaKeyMaterial(RSA rsa, RsaSecurityKey securityKey)
        {
            _rsa = rsa;
            SecurityKey = securityKey;
        }

        public RsaSecurityKey SecurityKey { get; }

        public static RsaKeyMaterial Create(string keyId)
        {
            var rsa = RSA.Create(2048);
            var securityKey = new RsaSecurityKey(rsa)
            {
                KeyId = keyId
            };

            return new RsaKeyMaterial(rsa, securityKey);
        }

        public string CreateJwksResponse()
        {
            var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(SecurityKey);
            key.Kid = SecurityKey.KeyId;
            return $$"""
            {
              "keys": [
                {{JsonSerializer.Serialize(key)}}
              ]
            }
            """;
        }

        public void Dispose()
        {
            _rsa.Dispose();
        }
    }
}
