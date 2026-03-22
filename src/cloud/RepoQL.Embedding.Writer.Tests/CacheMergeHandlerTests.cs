using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Embedding.Writer.Tests;

public sealed class CacheMergeHandlerTests
{
    [Test]
    public async Task TryParseStagingPath_ExtractsSourceAndModel()
    {
        var parsed = CacheMergeHandler.TryParseStagingPath(
            "source=abc123/model=voyage-context-3/instance-node-9f8e7d6c.parquet",
            out var pathInfo);

        await Assert.That(parsed).IsTrue();
        await Assert.That(pathInfo.Path).IsEqualTo("source=abc123/model=voyage-context-3/instance-node-9f8e7d6c.parquet");
        await Assert.That(pathInfo.SourceHash).IsEqualTo("abc123");
        await Assert.That(pathInfo.Model).IsEqualTo("voyage-context-3");
    }

    [Test]
    [Arguments("")]
    [Arguments("source=abc123/model=voyage-context-3")]
    [Arguments("source=abc123/model=/instance-node-9f8e7d6c.parquet")]
    [Arguments("source=abc123/model=voyage-context-3/part-123.parquet")]
    [Arguments("source=abc123/model=voyage-context-3/nested/instance-node-9f8e7d6c.parquet")]
    public async Task TryParseStagingPath_RejectsInvalidFormats(string path)
    {
        var parsed = CacheMergeHandler.TryParseStagingPath(path, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task MergeEndpoint_ReturnsOk_ForBadPathPayload()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"path":"not-a-staging-path"}"""));
        context.Response.Body = new MemoryStream();

        var result = await MergeEndpoint.HandleAsync(null, context, NullLoggerFactory.Instance);
        var statusCodeResult = result as IStatusCodeHttpResult;

        await Assert.That(statusCodeResult).IsNotNull();
        await Assert.That(statusCodeResult!.StatusCode).IsEqualTo(StatusCodes.Status200OK);
    }
}
