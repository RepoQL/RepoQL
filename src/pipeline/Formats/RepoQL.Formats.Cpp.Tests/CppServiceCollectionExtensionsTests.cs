using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Formats.Cpp.TreeSitter;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppServiceCollectionExtensionsTests
{
    [Test]
    public void AddCppFormat_RegistersDescriptorsAndProcessors()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCppFormat();

        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetServices<FormatDescriptor>().ToList();

        descriptors.Select(d => d.MediaType.Kind).Should().Contain(["code.c", "code.cpp", "code.cpp-header", "code.cpp-inline"]);
        descriptors.SelectMany(d => d.Labels).Should().Contain(["c", "h", "cpp", "hpp", "cc", "cxx", "hh", "hxx", "ipp", "tpp", "inl"]);

        provider.GetService<CppMaterializer>().Should().NotBeNull();
        provider.GetService<CppTreeSitterClient>().Should().NotBeNull();
        provider.GetService<CppXRayGenerator>().Should().NotBeNull();

        services.Any(sd => sd.ImplementationType == typeof(CppClassifier)).Should().BeTrue();
        services.Any(sd => sd.ImplementationType == typeof(CppParser)).Should().BeTrue();
    }

    [Test]
    public void SchemaProvider_ReturnsNoScriptsInPlan01()
    {
        var provider = new CppSchemaProvider();

        provider.GetSchemaScripts().Should().BeEmpty();
    }
}
