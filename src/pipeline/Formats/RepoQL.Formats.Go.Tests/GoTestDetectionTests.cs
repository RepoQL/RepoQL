using AwesomeAssertions;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoTestDetectionTests
{
    [Test]
    public async Task Materialize_TestFile_DetectsGoTestFunctions()
    {
        var records = await GoTestHelpers.LoadRecordsAsync("test_file_test.go");

        var tests = records.Annotations.Where(a => a.Kind == "go.test").ToList();
        tests.Should().HaveCount(5);
        tests.Should().Contain(a => a.Data["name"]!.ToString() == "TestAlpha" && a.Data["test_kind"]!.ToString() == "test");
        tests.Should().Contain(a => a.Data["name"]!.ToString() == "BenchmarkParser" && a.Data["test_kind"]!.ToString() == "benchmark");
        tests.Should().Contain(a => a.Data["name"]!.ToString() == "ExampleWidget" && a.Data["test_kind"]!.ToString() == "example");
        tests.Should().Contain(a => a.Data["name"]!.ToString() == "FuzzInput" && a.Data["test_kind"]!.ToString() == "fuzz");
        tests.Should().Contain(a => a.Data["name"]!.ToString() == "TestMain" && a.Data["test_kind"]!.ToString() == "testmain");

        var functionNodes = records.Nodes.Where(n => n.Kind == "go.function").ToList();
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "TestAlpha" && n.Props["test_kind"]!.ToString() == "test");
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "BenchmarkParser" && n.Props["test_kind"]!.ToString() == "benchmark");
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "ExampleWidget" && n.Props["test_kind"]!.ToString() == "example");
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "FuzzInput" && n.Props["test_kind"]!.ToString() == "fuzz");
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "TestMain" && n.Props["test_kind"]!.ToString() == "testmain");
        functionNodes.Should().Contain(n => n.Props["name"]!.ToString() == "Testhelper" && n.Props["test_kind"] == null);
    }

    [Test]
    public async Task Materialize_NonTestFile_DoesNotAnnotateTestFunctions()
    {
        var records = await GoTestHelpers.LoadRecordsAsync("test_file_test.go", artifactFileName: "sample.go");

        records.Annotations.Should().NotContain(a => a.Kind == "go.test");
        records.Nodes
            .Where(n => n.Kind == "go.function")
            .Should()
            .OnlyContain(n => n.Props["test_kind"] == null);
    }

    [Test]
    public async Task Materialize_InitFunctions_SetsIsInitProperty()
    {
        var records = await GoTestHelpers.LoadRecordsAsync("init_functions.go");
        var functions = records.Nodes.Where(n => n.Kind == "go.function").ToList();

        functions.Should().HaveCount(3);
        functions.Should().Contain(n => n.Props["name"]!.ToString() == "Setup" && n.Props["is_init"]!.GetValue<bool>() == false);

        var initFunctions = functions.Where(n => n.Props["name"]!.ToString() == "init").ToList();
        initFunctions.Should().HaveCount(2);
        initFunctions.Should().OnlyContain(n => n.Props["is_init"]!.GetValue<bool>());
    }
}
