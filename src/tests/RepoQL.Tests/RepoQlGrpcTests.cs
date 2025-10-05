/*
 TODO: Move all of the host functionality out of RepoQL.ConsoleApp so it can be tested
 
 using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// client
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models; // grpc messages
using RepoQL.Data.DuckDB;
using RepoQL.Host.Services;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

public class RepoQlGrpcTests
{
    private sealed class TestServer : IAsyncDisposable
    {
        public required IHost Host { get; init; }
        public required IGraphStore Store { get; init; }
        public required string RepoPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Store.Dispose();
            Host.Dispose();
            try { Directory.Delete(RepoPath, recursive: true); } catch {
            // ignore
             }
        }
    }

    private sealed class ImmediateBarrier : IInitialIndexingBarrier
    {
        public Task InitialScanCompleted { get; } = Task.CompletedTask;
    }

    private static async Task<TestServer> StartServerAsync()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "repoql-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repoPath);

        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(webBuilder =>
        {
            webBuilder.UseTestServer();
            webBuilder.ConfigureServices(services =>
            {
                services.AddGrpc();
                var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: true);
                store.EnsureSchema();
                services.AddSingleton<IGraphStore>(store);
                // Satisfy RepoQlServiceImpl DI
                services.AddSingleton(new Host.Options.RepositoryConfiguration { Path = repoPath });
                services.AddSingleton(new HostState { RepositoryPath = repoPath, ImplicitStart = false, StartedAtUtc = DateTime.UtcNow });
                services.AddSingleton<IInitialIndexingBarrier, ImmediateBarrier>();
            });
            webBuilder.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => { endpoints.MapGrpcService<RepoQlServiceImpl>(); });
            });
        });

        var host = builder.Build();
        await host.StartAsync();

        var store = host.Services.GetRequiredService<IGraphStore>();
        return new TestServer { Host = host, Store = store, RepoPath = repoPath };
    }

    [Test]
    public async Task RawQuery_WorksOverUnixSocket()
    {
        await using var server = await StartServerAsync();

        // In-memory client via TestServer channel
        var testServer = server.Host.GetTestServer();
        var httpClient = testServer.CreateClient();
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost", new Grpc.Net.Client.GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        await using var client = RepoQlClient.FromChannel(channel);
        var result = await client.ExecuteRawQueryAsync("select 1 as c");

        result.Columns.Count.Should().Be(1);
        result.Columns[0].Name.Should().Be("c");
        result.RowCount.Should().Be(1);
        result.Rows[0].Values[0].NumberValue.Should().Be(1);
    }

    [Test]
    public async Task GetDocumentSummaries_ReturnsOutlineAnnotation()
    {
        await using var server = await StartServerAsync();
        // Seed document + outline annotation
        var uri = RepoUri.Parse("file:///repo/test.md");

        // Insert minimal artifact + document
        var art = new Artifact { Id = Guid.NewGuid(), Digest = "xxh64:0123456789abcdef", Size = 10 };
        server.Store.UpsertArtifact(art);
        var doc = server.Store.UpsertDocumentByUri(uri, new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            ArtifactId = art.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        server.Store.UpsertAnnotation(new Annotation
        {
            SemanticKey = $"outline:{uri.AbsoluteUri.ToLowerInvariant()}",
            Kind = "outline",
            Severity = "info",
            Source = "test",
            Message = uri.AbsoluteUri + "\n- Heading",
            Data = new System.Text.Json.Nodes.JsonObject { ["lines"] = new System.Text.Json.Nodes.JsonArray(uri.AbsoluteUri, "- Heading") },
            ScopeDocumentId = doc.Id
        });

        var testServer = server.Host.GetTestServer();
        var httpClient = testServer.CreateClient();
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost", new Grpc.Net.Client.GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        await using var client = RepoQlClient.FromChannel(channel);
        var resp = await client.GetDocumentSummariesAsync([uri.AbsoluteUri], annotationKinds: ["outline"], includeMessage: true, includeData: true);

        resp.Results.Count.Should().Be(1);
        var res = resp.Results[0];
        res.Uri.Should().Be(uri.AbsoluteUri);
        res.Status.Should().Be(SummaryStatus.Ok);
        res.Annotations.Count.Should().Be(1);
        res.Annotations[0].Kind.Should().Be("outline");
        res.Annotations[0].Message.Should().StartWith(uri.AbsoluteUri);
    }

    [Test]
    public async Task RawQueryStream_EmitsSchemaThenRows()
    {
        await using var server = await StartServerAsync();

        var testServer = server.Host.GetTestServer();
        var httpClient = testServer.CreateClient();
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost", new Grpc.Net.Client.GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        await using var client = RepoQlClient.FromChannel(channel);

        var rows = new List<double>();
        var first = true;
        await foreach (var msg in client.ExecuteRawQueryStreamAsync("select 1 as c union all select 2 union all select 3"))
        {
            if (first)
            {
                msg.Columns.Count.Should().Be(1);
                msg.Columns[0].Name.Should().Be("c");
                first = false;
            }
            msg.Row.Values.Count.Should().Be(1);
            rows.Add(msg.Row.Values[0].NumberValue);
        }

        rows.Should().BeEquivalentTo([1d, 2d, 3d]);
    }
}
*/