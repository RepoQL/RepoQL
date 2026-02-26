using AwesomeAssertions;
using RepoQL.Protocol;

namespace RepoQL.Protocol.Tests;

public class ImportResultTests
{
    [Test]
    [DisplayName("HasOperationProgress is true when operation_id is present")]
    public void HasOperationProgress_True_WhenOperationIdPresent()
    {
        var result = new ImportResult(
            Status: new RepoQL.Contracts.PipelineStatus(),
            TotalFiles: 0,
            IndexedCount: 0,
            EmbeddedCount: 0,
            FailedCount: 0,
            Message: "Import started",
            OperationId: "op-123");

        result.HasOperationProgress.Should().BeTrue();
    }

    [Test]
    [DisplayName("ImportResult keeps message and operation_id values")]
    public void ImportResult_ExposesMessageAndOperationId()
    {
        var result = new ImportResult(
            Status: new RepoQL.Contracts.PipelineStatus(),
            TotalFiles: 12,
            IndexedCount: 2,
            EmbeddedCount: 1,
            FailedCount: 0,
            Message: "Importing 12 files from github://owner/repo - operation op-123",
            OperationId: "op-123");

        result.Message.Should().Be("Importing 12 files from github://owner/repo - operation op-123");
        result.OperationId.Should().Be("op-123");
    }
}
