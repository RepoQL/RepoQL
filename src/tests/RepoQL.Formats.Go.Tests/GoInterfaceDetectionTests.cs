using AwesomeAssertions;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoInterfaceDetectionTests
{
    private const string PackageName = "sample";

    [Test]
    public void Struct_WithMatchingMethods_SatisfiesInterface()
    {
        var service = Type("Service", "struct");
        var handler = Type("Handler", "interface");
        var methods = new[]
        {
            Method(service, "Handle", parameters: "(ctx Context)"),
            Method(handler, "Handle", parameters: "(ctx Context)")
        };

        var result = Compute([service, handler], methods, [], [service.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == service.Id
            && m.InterfaceNodeId == handler.Id
            && m.InterfaceQualifiedName == handler.QualifiedName
            && m.ReceiverKind == "value"
            && !m.IsStdlib);
    }

    [Test]
    public void Struct_MissingMethod_DoesNotSatisfyInterface()
    {
        var service = Type("Service", "struct");
        var handler = Type("Handler", "interface");
        var methods = new[]
        {
            Method(service, "Handle", parameters: "(ctx Context)"),
            Method(handler, "Handle", parameters: "(ctx Context)"),
            Method(handler, "Validate", parameters: "(ctx Context)")
        };

        var result = Compute([service, handler], methods, [], [service.Id]);

        result.Implementations.Should().NotContain(m =>
            m.TypeNodeId == service.Id
            && m.InterfaceNodeId == handler.Id);
    }

    [Test]
    public void PointerReceiverOnly_SatisfiesPointerMethodSet()
    {
        var worker = Type("Worker", "struct");
        var runner = Type("Runner", "interface");
        var methods = new[]
        {
            Method(worker, "Run", isPointerReceiver: true),
            Method(runner, "Run")
        };

        var result = Compute([worker, runner], methods, [], [worker.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == worker.Id
            && m.InterfaceNodeId == runner.Id
            && m.ReceiverKind == "pointer");
    }

    [Test]
    public void ValueReceiver_SatisfiesAsValueWhenBothWouldMatch()
    {
        var worker = Type("Worker", "struct");
        var runner = Type("Runner", "interface");
        var methods = new[]
        {
            Method(worker, "Run", isPointerReceiver: false),
            Method(runner, "Run")
        };

        var result = Compute([worker, runner], methods, [], [worker.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == worker.Id
            && m.InterfaceNodeId == runner.Id
            && m.ReceiverKind == "value");
    }

    [Test]
    public void EmbeddedStruct_PromotesMethods()
    {
        var baseType = Type("Base", "struct");
        var outerType = Type("Outer", "struct");
        var doer = Type("Doer", "interface");
        var methods = new[]
        {
            Method(baseType, "Do"),
            Method(doer, "Do")
        };
        var embeds = new[]
        {
            Embed(outerType, "Base")
        };

        var result = Compute([baseType, outerType, doer], methods, embeds, [outerType.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == outerType.Id
            && m.InterfaceNodeId == doer.Id
            && m.ReceiverKind == "value");
    }

    [Test]
    public void OuterMethod_ShadowsPromotedMethod()
    {
        var baseType = Type("Base", "struct");
        var outerType = Type("Outer", "struct");
        var runner = Type("Runner", "interface");
        var methods = new[]
        {
            Method(baseType, "Run", parameters: "(value int)"),
            Method(outerType, "Run", parameters: "()"),
            Method(runner, "Run", parameters: "(value int)")
        };
        var embeds = new[]
        {
            Embed(outerType, "Base")
        };

        var result = Compute([baseType, outerType, runner], methods, embeds, [outerType.Id]);

        result.Implementations.Should().NotContain(m =>
            m.TypeNodeId == outerType.Id
            && m.InterfaceNodeId == runner.Id);
    }

    [Test]
    public void EmbeddingCycle_IsDetected()
    {
        var first = Type("First", "struct");
        var second = Type("Second", "struct");
        var runnable = Type("Runnable", "interface");
        var methods = new[]
        {
            Method(first, "Run"),
            Method(runnable, "Run")
        };
        var embeds = new[]
        {
            Embed(first, "Second"),
            Embed(second, "First")
        };

        var result = Compute([first, second, runnable], methods, embeds, [first.Id]);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == "go.interface_satisfaction.cycle");
    }

    [Test]
    public void ErrorMethod_SatisfiesStdlibError()
    {
        var problem = Type("Problem", "struct");
        var methods = new[]
        {
            Method(problem, "Error")
        };

        var result = Compute([problem], methods, [], [problem.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == problem.Id
            && m.IsStdlib
            && m.InterfaceQualifiedName == "error"
            && m.InterfaceNodeId == null);
    }

    [Test]
    public void StdlibInterfaces_AreEvaluated()
    {
        var sortable = Type("Sortable", "struct");
        var methods = new[]
        {
            Method(sortable, "Len", parameters: "()"),
            Method(sortable, "Less", parameters: "(i, j int)"),
            Method(sortable, "Swap", parameters: "(i, j int)")
        };

        var result = Compute([sortable], methods, [], [sortable.Id]);

        result.Implementations.Should().ContainSingle(m =>
            m.TypeNodeId == sortable.Id
            && m.IsStdlib
            && m.InterfaceQualifiedName == "sort.Interface"
            && m.ReceiverKind == "value");
    }

    private static GoInterfaceSatisfactionResult Compute(
        IReadOnlyList<GoTypeSnapshot> types,
        IReadOnlyList<GoMethodSnapshot> methods,
        IReadOnlyList<GoEmbeddingSnapshot> embeddings,
        IReadOnlyCollection<Guid> candidateTypeIds)
        => GoInterfaceSatisfactionEngine.Compute(
            PackageName,
            types,
            methods,
            embeddings,
            candidateTypeIds);

    private static GoTypeSnapshot Type(string name, string kind)
        => new(Guid.NewGuid(), name, $"{PackageName}.{name}", kind);

    private static GoMethodSnapshot Method(
        GoTypeSnapshot declaringType,
        string name,
        bool isPointerReceiver = false,
        string? parameters = "()")
        => new(name, declaringType.QualifiedName, isPointerReceiver, parameters);

    private static GoEmbeddingSnapshot Embed(GoTypeSnapshot sourceType, string target)
        => new(sourceType.Id, target);
}
