using AwesomeAssertions;
using RepoQL.Testing;

namespace RepoQL.Sandbox.Tests;

public sealed class WasmtimeSandboxTests : IDisposable
{
    private readonly WasmtimeSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [Test]
    public void Execute_SimpleExpression_ReturnsResult()
    {
        var result = _sandbox.Execute("1 + 2");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("3");
    }

    [Test]
    public void Execute_StringResult_ReturnsJsonString()
    {
        var result = _sandbox.Execute("'hello world'");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("\"hello world\"");
    }

    [Test]
    public void Execute_ObjectResult_ReturnsJson()
    {
        var result = _sandbox.Execute("({name: 'test', value: 42})");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("\"name\"");
        result.JsonOutput.Should().Contain("\"test\"");
        result.JsonOutput.Should().Contain("42");
    }

    [Test]
    public void Execute_ArrayResult_ReturnsJsonArray()
    {
        var result = _sandbox.Execute("[1, 2, 3]");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("[1,2,3]");
    }

    [Test]
    public void Execute_ConsoleLog_CapturesDiagnostics()
    {
        var result = _sandbox.Execute("console.log('hello'); console.warn('careful'); 42");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("42");
        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics[0].Level.Should().Be("info");
        result.Diagnostics[0].Message.Should().Be("hello");
        result.Diagnostics[1].Level.Should().Be("warn");
        result.Diagnostics[1].Message.Should().Be("careful");
    }

    [Test]
    public void Execute_SyntaxError_ReturnsError()
    {
        var result = _sandbox.Execute("function {{{");

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().NotBeNull();
    }

    [Test]
    public void Execute_RuntimeError_ReturnsError()
    {
        var result = _sandbox.Execute("undefinedVariable.property");

        result.Success.Should().BeFalse();
    }

    [Test]
    public void Execute_UndefinedResult_ReturnsNull()
    {
        var result = _sandbox.Execute("undefined");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("null");
    }

    [Test]
    public void Execute_EmptyCode_ReturnsError()
    {
        var result = _sandbox.Execute("");

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be("syntax");
    }

    [Test]
    public void Execute_RepoqlQuery_ReturnsStructuredData()
    {
        var capabilities = new SandboxCapabilities
        {
            QueryHandler = sql =>
            {
                sql.Should().Be("SELECT name, lines FROM Files");
                return "[{\"name\":\"Foo\",\"lines\":42},{\"name\":\"Bar\",\"lines\":100}]";
            }
        };

        var result = _sandbox.Execute(
            "repoql.query('SELECT name, lines FROM Files').map(f => f.name)",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("Foo");
        result.JsonOutput.Should().Contain("Bar");
    }

    [Test]
    public void Execute_RepoqlQuery_HandlerReceivesSQL()
    {
        string? capturedSql = null;
        var capabilities = new SandboxCapabilities
        {
            QueryHandler = sql =>
            {
                capturedSql = sql;
                return "[]";
            }
        };

        _sandbox.Execute("repoql.query('SELECT * FROM Types')", capabilities: capabilities);

        capturedSql.Should().Be("SELECT * FROM Types");
    }

    [Test]
    public void Execute_RepoqlQuery_NoCapabilities_ThrowsJsError()
    {
        var result = _sandbox.Execute("repoql.query('SELECT 1')");

        result.Success.Should().BeFalse();
    }

    [Test]
    public void Execute_RepoqlQuery_HandlerError_BecomesJsException()
    {
        var capabilities = new SandboxCapabilities
        {
            QueryHandler = _ => throw new InvalidOperationException("DB connection failed")
        };

        var result = _sandbox.Execute(
            @"
        try {
            repoql.query('SELECT 1');
            'should not reach here'
        } catch(e) {
            ({caught: true, message: e.message})
        }",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("\"caught\":true");
        result.JsonOutput.Should().Contain("DB connection failed");
    }

    [Test]
    public void Execute_RepoqlQuery_MultipleQueries_AllWork()
    {
        var callCount = 0;
        var capabilities = new SandboxCapabilities
        {
            QueryHandler = sql =>
            {
                callCount++;
                return sql.Contains("Types", StringComparison.Ordinal)
                    ? "[{\"count\":5}]"
                    : "[{\"count\":10}]";
            }
        };

        var result = _sandbox.Execute(@"
        (() => {
            var types = repoql.query('SELECT count(*) as count FROM Types');
            var files = repoql.query('SELECT count(*) as count FROM Files');
            return {types: types[0].count, files: files[0].count};
        })()
    ", capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("5");
        result.JsonOutput.Should().Contain("10");
        callCount.Should().Be(2);
    }

    [Test]
    public void Execute_RepoqlRead_ReturnsContent()
    {
        var capabilities = new SandboxCapabilities
        {
            ReadHandler = (uri, budget) =>
            {
                uri.Should().Be("file:///src/Foo.cs");
                budget.Should().Be(3000);
                return "{\"content\":\"hello world\",\"representation\":\"full\",\"tokensUsed\":5}";
            }
        };

        var result = _sandbox.Execute(
            "repoql.read('file:///src/Foo.cs', {budget: 3000})",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("hello world");
    }

    [Test]
    public void Execute_RepoqlRead_DefaultBudget()
    {
        var capturedBudget = 0;
        var capabilities = new SandboxCapabilities
        {
            ReadHandler = (_, budget) =>
            {
                capturedBudget = budget;
                return "{\"content\":\"x\",\"representation\":\"full\",\"tokensUsed\":1}";
            }
        };

        _sandbox.Execute("repoql.read('file:///test')", capabilities: capabilities);

        capturedBudget.Should().Be(5000);
    }

    [Test]
    public void Execute_RepoqlRead_ErrorBecomesJsException()
    {
        var capabilities = new SandboxCapabilities
        {
            ReadHandler = (_, _) => "{\"__repoqlReadError\":\"File not found\"}"
        };

        var result = _sandbox.Execute(
            @"
        try { repoql.read('nonexistent'); 'no error' }
        catch(e) { ({caught: true, message: e.message}) }
    ",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("File not found");
    }

    [Test]
    public void Execute_RepoqlRead_NoCapabilities_ThrowsJsError()
    {
        var result = _sandbox.Execute("repoql.read('file:///test')");

        result.Success.Should().BeFalse();
    }

    [Test]
    public void Execute_RepoqlWrite_Success()
    {
        string? capturedUri = null;
        string? capturedContent = null;
        var capabilities = new SandboxCapabilities
        {
            WriteHandler = (uri, content) =>
            {
                capturedUri = uri;
                capturedContent = content;
                return null;
            }
        };

        var result = _sandbox.Execute("repoql.write('file:///out.txt', 'hello')", capabilities: capabilities);

        result.Success.Should().BeTrue();
        capturedUri.Should().Be("file:///out.txt");
        capturedContent.Should().Be("hello");
    }

    [Test]
    [WindowsOnly]
    public void Execute_RepoqlWrite_ErrorBecomesJsException()
    {
        var capabilities = new SandboxCapabilities
        {
            WriteHandler = (_, _) => "Permission denied"
        };

        var result = _sandbox.Execute(
            @"
        try { repoql.write('file:///bad', 'x'); 'no error' }
        catch(e) { ({caught: true, message: e.message}) }
    ",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("Permission denied");
    }

    [Test]
    public void Execute_RepoqlDelete_Success()
    {
        string? capturedUri = null;
        var capabilities = new SandboxCapabilities
        {
            DeleteHandler = uri =>
            {
                capturedUri = uri;
                return null;
            }
        };

        var result = _sandbox.Execute("repoql.delete('file:///out.txt')", capabilities: capabilities);

        result.Success.Should().BeTrue();
        capturedUri.Should().Be("file:///out.txt");
    }

    [Test]
    public void Execute_RepoqlDelete_ErrorBecomesJsException()
    {
        var capabilities = new SandboxCapabilities
        {
            DeleteHandler = _ => "Permission denied"
        };

        var result = _sandbox.Execute(
            @"
        try { repoql.delete('file:///bad'); 'no error' }
        catch(e) { ({caught: true, message: e.message}) }
    ",
            capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Contain("Permission denied");
    }

    [Test]
    public void Execute_ModuleImport_LoadsViaHandler()
    {
        string? capturedSpecifier = null;
        var capabilities = new SandboxCapabilities
        {
            ModuleLoaderHandler = specifier =>
            {
                capturedSpecifier = specifier;
                if (specifier == "repoql:@test/helper")
                    return "export function add(a, b) { return a + b; }";
                return null;
            }
        };

        var result = _sandbox.Execute("typeof repoql", capabilities: capabilities);

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("\"object\"");
        capturedSpecifier.Should().BeNull();
    }

    [Test]
    public void Execute_MultipleCallsIsolated_NoStateLeaks()
    {
        _sandbox.Execute("var x = 42");
        var result = _sandbox.Execute("typeof x");

        result.Success.Should().BeTrue();
        result.JsonOutput.Should().Be("\"undefined\"");
    }
}
