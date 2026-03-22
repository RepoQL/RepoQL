using AwesomeAssertions;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppXRayGeneratorTests
{
    [Test]
    public void Headline_Generation_MatchesPlanFormat()
    {
        var generator = new CppXRayGenerator();
        var output = generator.Generate(new CppXRayModel(
            FileName: "connection_pool.h",
            MediaKind: "code.cpp-header",
            LineCount: 180,
            TokenCount: 1000,
            PrimaryNamespace: "net",
            TopLevelTypes: ["ConnectionPool"],
            TopLevelFunctions: ["connect", "execute", "disconnect"],
            StructureLines: ["+ class ConnectionPool"]));

        output.Headline.Should().Contain("connection_pool.h | code.cpp-header | 180 ln");
        output.Headline.Should().Contain("ns:net");
        output.Headline.Should().Contain("class ConnectionPool");
        output.Headline.Should().Contain("connect, execute, disconnect");
    }

    [Test]
    public void Structure_Generation_PreservesVisibilityPrefixes()
    {
        var generator = new CppXRayGenerator();
        var output = generator.Generate(new CppXRayModel(
            FileName: "connection_pool.h",
            MediaKind: "code.cpp-header",
            LineCount: 40,
            TokenCount: 250,
            PrimaryNamespace: "net",
            TopLevelTypes: ["ConnectionPool"],
            TopLevelFunctions: [],
            StructureLines:
            [
                "+ class ConnectionPool",
                "  + void connect(const std::string& endpoint)",
                "  - int port",
                "  # virtual void shutdown() final"
            ]));

        output.Structure.Should().Contain("+ class ConnectionPool");
        output.Structure.Should().Contain("  + void connect(const std::string& endpoint)");
        output.Structure.Should().Contain("  - int port");
        output.Structure.Should().Contain("  # virtual void shutdown() final");
    }

    [Test]
    public void Headline_Generation_AppendsMacroWarning_WhenPresent()
    {
        var generator = new CppXRayGenerator();
        var output = generator.Generate(new CppXRayModel(
            FileName: "widget.h",
            MediaKind: "code.cpp-header",
            LineCount: 42,
            TokenCount: 256,
            PrimaryNamespace: "ui",
            TopLevelTypes: ["Widget"],
            TopLevelFunctions: [],
            StructureLines: ["+ class Widget"],
            MacroWarning: "Q_OBJECT"));

        output.Headline.Should().Contain("⚠ Q_OBJECT (hidden members)");
    }
}
