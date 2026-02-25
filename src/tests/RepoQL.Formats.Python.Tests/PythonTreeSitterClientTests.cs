using AwesomeAssertions;
using RepoQL.Formats.Python.Surface;
using RepoQL.Formats.Python.TreeSitter;

namespace RepoQL.Formats.Python.Tests;

public sealed class PythonTreeSitterClientTests
{
    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new PythonTreeSitterClient();
        var action = () => client.Parse(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_Empty_ReturnsEmptySurface()
    {
        using var client = new PythonTreeSitterClient();

        var result = client.Parse(string.Empty);

        result.Classes.Should().BeEmpty();
        result.Functions.Should().BeEmpty();
        result.Imports.Should().BeEmpty();
        result.Constants.Should().BeEmpty();
        result.TypeAliases.Should().BeEmpty();
        result.AllExports.Should().BeNull();
        result.ModuleDocstring.Should().BeNull();
        result.MetaprogrammingHints.Should().BeEmpty();
        result.FrameworkHints.Should().BeEmpty();
        result.ErrorNodeCount.Should().Be(0);
    }

    [Test]
    public void Parse_SimpleClass_ExtractsStructure()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("simple_class.py");

        var result = client.Parse(source);

        result.Classes.Should().ContainSingle(c => c.Name == "User");
        var klass = result.Classes.Single(c => c.Name == "User");
        klass.QualifiedName.Should().Be("User");
        klass.BaseClasses.Should().Contain(["BaseUser", "Trackable"]);
        klass.Metaclass.Should().Be("ABCMeta");
        klass.Decorators.Should().ContainSingle(d => d.Name == "decorators.model" && d.Arguments == "(\"user\")");
        klass.Docstring.Should().Contain("Simple user class");
        klass.Slots.Should().Contain("name");
        klass.Methods.Select(m => m.Name).Should().Contain(["build", "__init__", "greet"]);
        klass.ClassVariables.Select(v => v.Name).Should().Contain(["KIND", "level", "__slots__"]);
        klass.InstanceVariables.Select(v => v.Name).Should().Contain(["name", "email", "active"]);
        klass.InstanceVariables.Single(v => v.Name == "name").TypeAnnotation.Should().Be("str");
        klass.InstanceVariables.Single(v => v.Name == "email").TypeAnnotation.Should().Be("str");

        result.Imports.Should().Contain(i => i.Module == "os");
        result.Functions.Should().ContainSingle(f => f.Name == "helper");
        result.ModuleDocstring.Should().BeNull();
    }

    [Test]
    public void Parse_DataclassClass_ExtractsFields()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("dataclass_example.py");

        var result = client.Parse(source);

        var klass = result.Classes.Single(c => c.Name == "Point");
        klass.Decorators.Should().ContainSingle(d => d.Name == "dataclass");
        klass.ClassVariables.Select(v => v.Name).Should().Contain(["x", "y"]);
        klass.ClassVariables.Single(v => v.Name == "x").TypeAnnotation.Should().Be("int");
    }

    [Test]
    public void Parse_EnumClass_ExtractsMembers()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("enum_example.py");

        var result = client.Parse(source);

        var klass = result.Classes.Single(c => c.Name == "Status");
        klass.BaseClasses.Should().Contain("Enum");
        klass.ClassVariables.Select(v => v.Name).Should().Contain(["READY", "BUSY"]);
    }

    [Test]
    public void Parse_ProtocolClass_ExtractsProtocol()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("protocol_example.py");

        var result = client.Parse(source);

        var klass = result.Classes.Single(c => c.Name == "Greeter");
        klass.BaseClasses.Should().Contain("Protocol");
        klass.Methods.Should().ContainSingle(m => m.Name == "greet" && m.ReturnType == "str");
    }

    [Test]
    public void Parse_NestedClasses_ExtractsQualifiedNames()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("nested_classes.py");

        var result = client.Parse(source);

        result.Classes.Should().Contain(c => c.QualifiedName == "Outer");
        result.Classes.Should().Contain(c => c.QualifiedName == "Outer.Inner");
    }

    [Test]
    public void Parse_Decorators_ExtractsNameAndArguments()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("decorators.py");

        var result = client.Parse(source);
        var klass = result.Classes.Single(c => c.Name == "DecoratorTarget");

        klass.Methods.Single(m => m.Name == "prop").Decorators.Should().ContainSingle(d => d.Name == "property");
        klass.Methods.Single(m => m.Name == "static").Decorators.Should().ContainSingle(d => d.Name == "staticmethod");
        klass.Methods.Single(m => m.Name == "from_value").Decorators.Should().ContainSingle(d => d.Name == "classmethod");
        result.Functions.Where(f => f.Name == "pick")
            .SelectMany(f => f.Decorators)
            .Should()
            .Contain(d => d.Name == "custom.decorator" && d.Arguments!.Contains("enabled=True", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_TypeAnnotations_ExtractsParameterAndReturnTypes()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("type_annotations.py");

        var result = client.Parse(source);

        var typed = result.Functions.Single(f => f.Name == "typed");
        typed.ReturnType.Should().Be("Optional[str]");
        typed.Parameters.Single(p => p.Name == "value").Type.Should().Be("int");
        typed.Parameters.Single(p => p.Name == "name").Default.Should().Be("\"a\"");
        typed.Parameters.Single(p => p.Name == "extras").Kind.Should().Be(PythonParameterKind.VarKeyword);

        var separators = result.Functions.Single(f => f.Name == "separators");
        separators.Parameters.Single(p => p.Name == "a").Kind.Should().Be(PythonParameterKind.PositionalOnly);
        separators.Parameters.Single(p => p.Name == "b").Kind.Should().Be(PythonParameterKind.PositionalOrKeyword);
        separators.Parameters.Single(p => p.Name == "c").Kind.Should().Be(PythonParameterKind.KeywordOnly);
        separators.Parameters.Single(p => p.Name == "kwargs").Kind.Should().Be(PythonParameterKind.VarKeyword);
    }

    [Test]
    public void Parse_InstanceVariables_ExtractsFromInit()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("instance_variables.py");

        var result = client.Parse(source);

        var klass = result.Classes.Single(c => c.Name == "Example");
        klass.InstanceVariables.Select(v => v.Name).Should().Contain(["name", "count", "cache"]);
        klass.InstanceVariables.Should().NotContain(v => v.Name == "other");
        klass.InstanceVariables.Single(v => v.Name == "name").TypeAnnotation.Should().Be("str");
        klass.InstanceVariables.Single(v => v.Name == "count").TypeAnnotation.Should().Be("int");
    }

    [Test]
    public void Parse_ClassVariables_ExtractsWithTypes()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("class_variables.py");

        var result = client.Parse(source);

        var klass = result.Classes.Single(c => c.Name == "Config");
        klass.ClassVariables.Select(v => v.Name).Should().Contain(["enabled", "retries", "threshold"]);
        klass.ClassVariables.Single(v => v.Name == "enabled").TypeAnnotation.Should().Be("bool");
        klass.ClassVariables.Single(v => v.Name == "threshold").TypeAnnotation.Should().Be("float");
    }

    [Test]
    public void Parse_ImportsBasic_ExtractsModulesAndNames()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("imports_basic.py");

        var result = client.Parse(source);

        result.Imports.Should().Contain(i => i.Module == "os" && i.Names.Count == 0);
        result.Imports.Should().Contain(i => i.Module == "sys" && i.Names.Any(n => n.Alias == "system"));
        result.Imports.Should().Contain(i => i.Module == "collections" && i.Names.Any(n => n.Name == "deque"));
        result.Imports.Should().Contain(i => i.Module == "collections" && i.Names.Any(n => n.Name == "defaultdict" && n.Alias == "dd"));
        result.Imports.Should().Contain(i => i.Module == "pathlib" && i.IsStar);
    }

    [Test]
    public void Parse_ImportsRelative_ExtractsLevels()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("imports_relative.py");

        var result = client.Parse(source);

        result.Imports.Should().Contain(i => i.IsRelative && i.RelativeLevel == 1 && i.Module == null);
        result.Imports.Should().Contain(i => i.IsRelative && i.RelativeLevel == 2 && i.Module == "core");
        result.Imports.Should().Contain(i => i.IsRelative && i.RelativeLevel == 3 && i.Module == "pkg.mod");
    }

    [Test]
    public void Parse_ImportsTypeChecking_DetectsGuard()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("imports_type_checking.py");

        var result = client.Parse(source);

        result.Imports.Should().Contain(i => i.IsTypeCheckingOnly && i.Module == "app.models");
        result.Imports.Should().Contain(i => i.IsTypeCheckingOnly && i.Module == "external.package");
        result.Imports.Should().Contain(i => !i.IsTypeCheckingOnly && i.Module == "runtime_only");
    }
    [Test]
    public void Parse_Docstrings_ExtractsModuleClassMethod()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("docstrings.py");

        var result = client.Parse(source);

        result.ModuleDocstring.Should().Be("Module docs.");
        var klass = result.Classes.Single(c => c.Name == "Documented");
        klass.Docstring.Should().Be("Class docs.");
        klass.Methods.Single(m => m.Name == "run").Docstring.Should().Be("Method docs.");
    }

    [Test]
    public void Parse_Constants_ExtractsFinalAndAllCaps()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("constants.py");

        var result = client.Parse(source);

        result.Constants.Should().Contain(c => c.Name == "MAX_SIZE" && c.IsFinal && c.IsAllCaps);
        result.Constants.Should().Contain(c => c.Name == "TIMEOUT" && !c.IsFinal && c.IsAllCaps);
        result.Constants.Should().Contain(c => c.Name == "value" && !c.IsAllCaps);
    }

    [Test]
    public void Parse_TypeAliases_ExtractsBothForms()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("type_aliases.py");

        var result = client.Parse(source);

        result.TypeAliases.Should().Contain(a => a.Name == "UserId" && a.Definition == "int");
        result.TypeAliases.Should().Contain(a => a.Name == "JsonDict" && a.Definition == "dict[str, object]");
    }

    [Test]
    public void Parse_AllExports_ExtractsNames()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("constants.py");

        var result = client.Parse(source);

        result.AllExports.Should().NotBeNull();
        result.AllExports.Should().Contain(["MAX_SIZE", "TIMEOUT"]);
    }

    [Test]
    public void Parse_Visibility_DetectsConventions()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("visibility_conventions.py");

        var result = client.Parse(source);
        var klass = result.Classes.Single(c => c.Name == "Visibility");

        PythonTreeSitterClient.DetermineVisibility("public").Should().Be("public");
        PythonTreeSitterClient.DetermineVisibility("_private").Should().Be("private");
        PythonTreeSitterClient.DetermineVisibility("__mangled").Should().Be("private");
        PythonTreeSitterClient.DetermineVisibility("__dunder__").Should().Be("public");

        klass.Methods.Select(m => m.Name).Should().Contain(["public", "_private", "__mangled", "__dunder__"]);
    }

    [Test]
    public void Parse_AsyncFunctions_DetectsAsyncAndGenerators()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("async_functions.py");

        var result = client.Parse(source);

        var manager = result.Functions.Single(f => f.Name == "manager");
        manager.IsAsync.Should().BeTrue();
        manager.IsGenerator.Should().BeTrue();
        manager.IsAsyncGenerator.Should().BeTrue();

        var stream = result.Functions.Single(f => f.Name == "stream");
        stream.IsAsync.Should().BeTrue();
        stream.IsGenerator.Should().BeTrue();
        stream.IsAsyncGenerator.Should().BeTrue();

        var regular = result.Functions.Single(f => f.Name == "regular");
        regular.IsAsync.Should().BeTrue();
        regular.IsGenerator.Should().BeFalse();
    }

    [Test]
    public void Parse_AsyncWithFor_DetectsUsage()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("async_functions.py");

        var result = client.Parse(source);

        var stream = result.Functions.Single(f => f.Name == "stream");
        stream.UsesAsyncWith.Should().BeTrue();
        stream.UsesAsyncFor.Should().BeTrue();
    }

    [Test]
    public void Parse_Generators_DetectsYield()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("generators.py");

        var result = client.Parse(source);

        result.Functions.Single(f => f.Name == "numbers").IsGenerator.Should().BeTrue();
        result.Functions.Single(f => f.Name == "combine").IsGenerator.Should().BeTrue();
    }

    [Test]
    public void Parse_Malformed_ReportsErrorsAndPartialResults()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("malformed.py");

        var result = client.Parse(source);

        result.ErrorNodeCount.Should().BeGreaterThan(0);
        result.Classes.Should().Contain(c => c.Name == "Recovered");
    }

    [Test]
    public void Parse_Metaprogramming_DetectsPatterns()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("metaprogramming.py");

        var result = client.Parse(source);

        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "exec");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "eval");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "type_dynamic_class");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "setattr");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "__import__");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "__getattr__");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "__getattr___module");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "__dir___module");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "importlib.import_module");
    }

    [Test]
    public void Parse_FrameworkPatterns_DetectsOrmFields()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("framework_django_model.py");

        var result = client.Parse(source);

        result.FrameworkHints.Should().Contain(h => h.RuleId == "django_field");
        result.FrameworkHints.Should().Contain(h => h.RuleId == "sqlalchemy_column");
        result.FrameworkHints.Should().Contain(h => h.RuleId == "pydantic_field");
    }

    [Test]
    public void ExecuteQuery_ReturnsCapturesWithRanges()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("imports_basic.py");

        const string query = """
            (import_statement
                name: (dotted_name) @module_name) @import_statement
            """;

        var matches = client.ExecuteQuery(query, source);

        matches.Should().NotBeEmpty();
        matches.SelectMany(m => m.Captures).Should().Contain(c => c.Name == "module_name" && c.Text == "os");
        matches.SelectMany(m => m.Captures).All(c => c.ByteRange.EndByte > c.ByteRange.StartByte).Should().BeTrue();
    }

    private static string ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
