using AwesomeAssertions;
using FakeItEasy;
using RepoQL.ConsoleApp.CommandImplementations;

namespace RepoQL.Tests.CommandImplementations;

internal sealed class HeapMemoryCommandTests
{
    [Test]
    public async Task Execute_FormatsTopManagedTypes()
    {
        var ops = A.Fake<HeapMemoryCommand.IHeapMemoryCommandOperations>();
        A.CallTo(() => ops.EnsureHostAvailableAsync(A<CancellationToken>._)).Returns(ValueTask.CompletedTask);
        A.CallTo(() => ops.TryGetHostProcessId()).Returns(4242);
        A.CallTo(() => ops.CaptureManagedHeapSnapshot(4242, A<CancellationToken>._))
            .Returns(new HeapMemoryCommand.ManagedHeapSnapshot(
                4242,
                1280552,
                612L * 1024 * 1024,
                [
                    new HeapMemoryCommand.ManagedHeapTypeStat("System.Byte[]", 98240, 344L * 1024 * 1024, "LOH 96%, Gen2 4%"),
                    new HeapMemoryCommand.ManagedHeapTypeStat("System.String", 412118, 88L * 1024 * 1024, "Gen2 100%")
                ]));

        var command = new HeapMemoryCommand(ops);

        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Managed Heap");
        result.Text.Should().Contain("Host PID:");
        result.Text.Should().Contain("1,280,552");
        result.Text.Should().Contain("System.Byte[]");
        result.Text.Should().Contain("LOH 96%, Gen2 4%");
        result.Text.Should().Contain("shallow managed bytes only");
    }

    [Test]
    public async Task Execute_ReturnsErrorWhenHostPidCannotBeResolved()
    {
        var ops = A.Fake<HeapMemoryCommand.IHeapMemoryCommandOperations>();
        A.CallTo(() => ops.EnsureHostAvailableAsync(A<CancellationToken>._)).Returns(ValueTask.CompletedTask);
        A.CallTo(() => ops.TryGetHostProcessId()).Returns(null);

        var command = new HeapMemoryCommand(ops);

        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Could not determine the host PID");
    }
}
