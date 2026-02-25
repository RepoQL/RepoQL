using AwesomeAssertions;
using RepoQL.Formats.Go.TreeSitter;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoTreeSitterClientTests
{
    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new GoTreeSitterClient();
        var action = () => client.Parse(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_Empty_ReturnsEmptySurface()
    {
        using var client = new GoTreeSitterClient();

        var result = client.Parse(string.Empty);

        result.PackageName.Should().BeNull();
        result.Imports.Should().BeEmpty();
        result.Structs.Should().BeEmpty();
        result.Interfaces.Should().BeEmpty();
        result.TypeDefinitions.Should().BeEmpty();
        result.Constants.Should().BeEmpty();
        result.ConstantBlocks.Should().BeEmpty();
        result.Variables.Should().BeEmpty();
        result.Directives.Should().BeEmpty();
        result.Functions.Should().BeEmpty();
        result.InitFunctions.Should().BeEmpty();
        result.Methods.Should().BeEmpty();
        result.ErrorNodeCount.Should().Be(0);
    }

    [Test]
    public void Parse_SimpleStruct_ExtractsStructAndFields()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("simple_struct.go");

        var result = client.Parse(source);

        result.PackageName.Should().Be("main");
        result.Structs.Should().ContainSingle(s => s.Name == "Server");
        var server = result.Structs.Single(s => s.Name == "Server");
        server.IsExported.Should().BeTrue();
        server.Fields.Select(f => f.Name).Should().Contain(["DB", "Logger", "Handler", "port"]);

        var db = server.Fields.Single(f => f.Name == "DB");
        db.TypeName.Should().Be("*sql.DB");
        db.Tag.Should().Be("json:\"db\" db:\"database\"");
        db.IsEmbedded.Should().BeFalse();
        db.IsExported.Should().BeTrue();

        var embedded = server.Fields.Single(f => f.Name == "Handler");
        embedded.TypeName.Should().Be("http.Handler");
        embedded.IsEmbedded.Should().BeTrue();
        embedded.IsExported.Should().BeTrue();

        var port = server.Fields.Single(f => f.Name == "port");
        port.IsExported.Should().BeFalse();
    }

    [Test]
    public void Parse_SimpleStruct_ExtractsMethods()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("simple_struct.go");

        var result = client.Parse(source);

        result.Methods.Select(m => m.Name).Should().Contain(["Serve", "String"]);

        var serve = result.Methods.Single(m => m.Name == "Serve");
        serve.ReceiverName.Should().Be("s");
        serve.ReceiverType.Should().Be("Server");
        serve.IsPointerReceiver.Should().BeTrue();
        serve.Parameters.Should().Be("(addr string)");
        serve.ReturnType.Should().Be("error");
        serve.IsExported.Should().BeTrue();

        var toString = result.Methods.Single(m => m.Name == "String");
        toString.ReceiverType.Should().Be("Server");
        toString.IsPointerReceiver.Should().BeFalse();
        toString.ReturnType.Should().Be("string");
    }

    [Test]
    public void Parse_SimpleStruct_ExtractsTopLevelFunctions()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("simple_struct.go");

        var result = client.Parse(source);

        result.Functions.Should().ContainSingle(f => f.Name == "NewServer");
        var factory = result.Functions.Single(f => f.Name == "NewServer");
        factory.IsExported.Should().BeTrue();
        factory.Parameters.Should().Be("(db *sql.DB)");
        factory.ReturnType.Should().Be("*Server");
    }

    [Test]
    public void Parse_Interfaces_ExtractsInterfacesAndMethods()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("interfaces.go");

        var result = client.Parse(source);

        result.Interfaces.Select(i => i.Name).Should().Contain(["Handler", "Middleware", "Closer"]);

        var handler = result.Interfaces.Single(i => i.Name == "Handler");
        handler.Methods.Select(m => m.Name).Should().Contain(["Handle", "Validate"]);
        handler.Methods.Single(m => m.Name == "Handle").Parameters.Should().Be("(ctx Context, req Request)");
        handler.Methods.Single(m => m.Name == "Handle").ReturnType.Should().Be("Response");
        handler.Methods.Single(m => m.Name == "Validate").ReturnType.Should().Be("error");

        var middleware = result.Interfaces.Single(i => i.Name == "Middleware");
        middleware.EmbeddedInterfaces.Should().Contain("Handler");
        middleware.Methods.Select(m => m.Name).Should().Contain(["Before", "After"]);
    }

    [Test]
    public void Parse_Imports_ExtractsSingleAndGrouped()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("imports.go");

        var result = client.Parse(source);

        result.Imports.Should().HaveCount(6);
        result.Imports.Select(i => i.Path).Should().Contain(
        [
            "fmt",
            "os",
            "path/filepath",
            "github.com/lib/pq",
            "github.com/onsi/gomega",
            "github.com/gorilla/mux"
        ]);

        result.Imports.Should().Contain(i => i.Alias == "_");
        result.Imports.Should().Contain(i => i.Alias == ".");
        result.Imports.Should().Contain(i => i.Alias == "router");
    }

    [Test]
    public void Parse_Imports_ClassifiesStdlibVsExternal()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("imports.go");

        var result = client.Parse(source);

        result.Imports.Single(i => i.Path == "fmt").Category.Should().Be("stdlib");
        result.Imports.Single(i => i.Path == "os").Category.Should().Be("stdlib");
        result.Imports.Single(i => i.Path == "path/filepath").Category.Should().Be("stdlib");
        result.Imports.Single(i => i.Path == "github.com/lib/pq").Category.Should().Be("external");
        result.Imports.Single(i => i.Path == "github.com/onsi/gomega").Category.Should().Be("external");
        result.Imports.Single(i => i.Path == "github.com/gorilla/mux").Category.Should().Be("external");
    }

    [Test]
    public void Parse_Embedding_ExtractsEmbeddedFields()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("embedding.go");

        var result = client.Parse(source);

        result.Structs.Select(s => s.Name).Should().Contain(["Base", "User"]);
        var user = result.Structs.Single(s => s.Name == "User");

        user.Fields.Should().Contain(f => f.Name == "Base" && f.IsEmbedded && f.TypeName == "Base");
        user.Fields.Should().Contain(f => f.Name == "Mutex" && f.IsEmbedded && f.TypeName == "sync.Mutex");
        user.Fields.Should().Contain(f => f.Name == "Email" && !f.IsEmbedded && f.TypeName == "string");
    }

    [Test]
    public void Parse_Malformed_ReturnsPartialResults()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("malformed.go");

        var result = client.Parse(source);

        result.ErrorNodeCount.Should().BeGreaterThan(0);
        result.Functions.Should().Contain(f => f.Name == "Valid");
        result.Structs.Should().Contain(s => s.Name == "AlsoValid");
    }

    [Test]
    public void Parse_Visibility_DetectsExportedUnexported()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("functions.go");

        var result = client.Parse(source);

        result.Functions.Single(f => f.Name == "ParseConfig").IsExported.Should().BeTrue();
        result.Functions.Single(f => f.Name == "formatOutput").IsExported.Should().BeFalse();
        result.Functions.Single(f => f.Name == "Setup").IsExported.Should().BeTrue();
    }

    [Test]
    public void Parse_TypeDefinitions_ExtractsAliasesAndDefinitions()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("type_definitions.go");

        var result = client.Parse(source);

        result.TypeDefinitions.Select(t => t.Name).Should().Contain(["UserID", "DisplayName", "Labels", "Counter"]);
        result.TypeDefinitions.Should().Contain(t => t.Name == "UserID" && !t.IsAlias && t.UnderlyingType == "int64");
        result.TypeDefinitions.Should().Contain(t => t.Name == "DisplayName" && t.IsAlias && t.UnderlyingType == "string");
        result.TypeDefinitions.Should().Contain(t => t.Name == "Labels" && t.IsAlias && t.UnderlyingType == "map[string]string");
        result.TypeDefinitions.Should().Contain(t => t.Name == "Counter" && !t.IsAlias && t.UnderlyingType == "uint32");
        result.TypeDefinitions.Should().NotContain(t => t.Name == "Service");
        result.TypeDefinitions.Should().NotContain(t => t.Name == "Runner");
    }

    [Test]
    public void Parse_Variables_DetectsSentinelErrorsAndInterfaceAssertions()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("variables.go");

        var result = client.Parse(source);

        result.Variables.Should().Contain(v => v.Name == "ErrClosed" && v.IsSentinelError);
        result.Variables.Should().Contain(v => v.Name == "ErrWrapped" && v.IsSentinelError);
        result.Variables.Should().Contain(v => v.Name == "ErrCustom" && v.IsSentinelError);
        result.Variables.Should().Contain(v => v.Name == "Count" && !v.IsSentinelError && !v.IsInterfaceAssertion);

        var assertions = result.Variables.Where(v => v.IsInterfaceAssertion).ToList();
        assertions.Should().HaveCount(2);
        assertions.Should().OnlyContain(v => v.Name == "_" && v.AssertedInterface == "Runner" && v.AssertedType == "Server");
    }

    [Test]
    public void Parse_Directives_ExtractsCompilerDirectives()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("directives.go");

        var result = client.Parse(source);

        result.Directives.Should().Contain(d => d.Kind == "build" && d.Text.Contains("go:build", StringComparison.Ordinal));
        result.Directives.Should().Contain(d => d.Kind == "generate" && d.Text.Contains("go:generate", StringComparison.Ordinal));
        result.Directives.Should().Contain(d => d.Kind == "embed" && d.Text.Contains("go:embed", StringComparison.Ordinal));
        result.Directives.Should().Contain(d => d.Kind == "linkname" && d.Text.Contains("go:linkname", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_Concurrency_ExtractsGoroutineChannelAndSelectMarkers()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("concurrency.go");

        var result = client.Parse(source);

        result.Directives.Should().Contain(d => d.Kind == "goroutine");
        result.Directives.Should().Contain(d => d.Kind == "channel");
        result.Directives.Should().Contain(d => d.Kind == "select");
    }

    [Test]
    public void Parse_InitFunctions_DetectsMultipleInitFunctions()
    {
        using var client = new GoTreeSitterClient();
        var source = ReadFixture("init_functions.go");

        var result = client.Parse(source);

        result.InitFunctions.Should().HaveCount(2);
        result.InitFunctions.Should().OnlyContain(f => f.Name == "init");
        result.Functions.Should().Contain(f => f.Name == "Setup");
    }

    [Test]
    public async Task Concurrent_Parsing_IsThreadSafe()
    {
        using var client = new GoTreeSitterClient();
        var sources = new[]
        {
            ReadFixture("simple_struct.go"),
            ReadFixture("interfaces.go"),
            ReadFixture("functions.go"),
            ReadFixture("imports.go"),
            ReadFixture("embedding.go"),
            ReadFixture("malformed.go"),
            "package sample\nfunc One() {}\n",
            "package sample\ntype Inline struct { Value int }\n"
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => client.Parse(sources[i])))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(8);
        results[0].Structs.Should().Contain(s => s.Name == "Server");
        results[1].Interfaces.Should().Contain(i => i.Name == "Handler");
        results[2].Functions.Should().Contain(f => f.Name == "ParseConfig");
        results[3].Imports.Should().HaveCount(6);
        results[4].Structs.Should().Contain(s => s.Name == "User");
        results[5].ErrorNodeCount.Should().BeGreaterThan(0);
        results[6].Functions.Should().ContainSingle(f => f.Name == "One");
        results[7].Structs.Should().ContainSingle(s => s.Name == "Inline");
    }

    private static string ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
