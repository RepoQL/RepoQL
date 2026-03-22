using AwesomeAssertions;
using RepoQL.Formats.Ruby.TreeSitter;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyTreeSitterClientTests
{
    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new RubyTreeSitterClient();
        var action = () => client.Parse(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_Empty_ReturnsEmptySurface()
    {
        using var client = new RubyTreeSitterClient();

        var result = client.Parse(string.Empty);

        result.Classes.Should().BeEmpty();
        result.Modules.Should().BeEmpty();
        result.Functions.Should().BeEmpty();
        result.Requires.Should().BeEmpty();
        result.Aliases.Should().BeEmpty();
        result.MetaprogrammingHints.Should().BeEmpty();
        result.ErrorNodeCount.Should().Be(0);
    }

    [Test]
    public void Parse_SimpleClass_ExtractsStructure()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("simple_class.rb");

        var result = client.Parse(source);

        result.Classes.Should().ContainSingle(c => c.Name == "User");
        var klass = result.Classes.Single(c => c.Name == "User");
        klass.Superclass.Should().Be("BaseUser");
        klass.HasSuperclassDeclaration.Should().BeTrue();
        klass.Methods.Select(m => m.Name).Should().Contain(["secret", "greet"]);
        klass.SingletonMethods.Select(m => m.Name).Should().Contain(["build", "from_json"]);
        klass.Mixins.Select(m => m.Mechanism).Should().Contain(["include", "extend", "prepend"]);
        klass.Mixins.Single(m => m.Mechanism == "include").Ordinal.Should().Be(0);
        klass.Attributes.Select(a => a.Name).Should().Contain(["name", "email"]);
        klass.Constants.Select(c => c.Name).Should().Contain("CONSTANT");
        klass.Methods.Single(m => m.Name == "secret").Visibility.Should().Be("private");
        klass.Methods.Single(m => m.Name == "greet").Visibility.Should().Be("public");
        klass.Methods.Single(m => m.Name == "secret").AcceptsBlock.Should().BeTrue();
        klass.ByteRange.EndByte.Should().BeGreaterThan(klass.ByteRange.StartByte);

        result.Modules.Should().ContainSingle(m => m.Name == "App");
        result.Aliases.Should().Contain(a => a.AliasType == "alias" && a.NewName == "username" && a.OriginalName == "name");
        result.Aliases.Should().Contain(a => a.AliasType == "alias_method" && a.NewName == "display_name" && a.OriginalName == "greet");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "define_method");
        result.Functions.Should().ContainSingle(f => f.Name == "top_level");
    }

    [Test]
    public void Parse_ModuleWithMethods_ExtractsModuleAndConstants()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("module_with_methods.rb");

        var result = client.Parse(source);

        result.Modules.Should().ContainSingle(m => m.Name == "Searchable");
        var mod = result.Modules.Single(m => m.Name == "Searchable");
        mod.Methods.Select(m => m.Name).Should().Contain(["search", "build_index"]);
        mod.Constants.Select(c => c.Name).Should().Contain("VERSION");
        mod.Mixins.Select(m => m.ModuleName).Should().Contain("Enumerable");
        mod.NestingDepth.Should().Be(0);
    }

    [Test]
    public void Parse_MixinGraph_ExtractsExtendSelfAndZeroBasedOrdinals()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("mixin_graph.rb");

        var result = client.Parse(source);

        var customer = result.Classes.Single(c => c.Name == "Customer");
        customer.Mixins.Select(m => m.Ordinal).Should().Equal([0, 1, 2, 3]);

        var formatting = result.Modules.Single(m => m.Name == "Formatting");
        formatting.Mixins.Should().Contain(m => m.Mechanism == "extend" && m.ModuleName == "self" && m.Ordinal == 0);
    }

    [Test]
    public void Parse_VisibilityModifiers_HandlesBareAndTargeted()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("visibility_modifiers.rb");

        var result = client.Parse(source);
        var klass = result.Classes.Single(c => c.Name == "VisibilityExample");
        klass.Methods.Single(m => m.Name == "open_method").Visibility.Should().Be("protected");
        klass.Methods.Single(m => m.Name == "private_method").Visibility.Should().Be("private");
    }

    [Test]
    public void Parse_ConstantsAndNamespaces_ExtractsQualifiedNames()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("constants_and_namespaces.rb");

        var result = client.Parse(source);
        result.Modules.Should().ContainSingle(m => m.QualifiedName == "Outer");
        result.Classes.Should().ContainSingle(c => c.QualifiedName == "Outer::Inner");
        result.Classes.Single().Constants.Select(c => c.Name).Should().Contain("VALUE");
    }

    [Test]
    public void Parse_Requires_ExtractsDependencies()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("require_dependencies.rb");

        var result = client.Parse(source);

        result.Requires.Should().Contain(r => !r.IsRelative && r.Path == "json");
        result.Requires.Should().Contain(r => r.IsRelative && r.Path == "../lib/support");
        result.Requires.All(r => r.ByteRange.EndByte > r.ByteRange.StartByte).Should().BeTrue();
    }

    [Test]
    public void Parse_Malformed_ReportsErrorsAndPartialResults()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("malformed.rb");

        var result = client.Parse(source);

        result.ErrorNodeCount.Should().BeGreaterThan(0);
        result.Classes.Select(c => c.Name).Should().Contain("Recovered");
    }

    [Test]
    public void Parse_UnextractableMetaprogramming_MarksDefineMethodAsNonExtractableAndDetectsMethodMissing()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("unextractable_metaprogramming.rb");

        var result = client.Parse(source);

        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "define_method" && !h.Extractable);
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "class_eval");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "module_eval");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "instance_eval");
        result.MetaprogrammingHints.Should().Contain(h => h.PatternName == "method_missing");
    }

    [Test]
    public void ExecuteQuery_ReturnsCapturesWithRanges()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("simple_class.rb");

        const string requireQuery = """
            (call method: (identifier) @req_method
                  arguments: (argument_list (string (string_content) @path))
             (#match? @req_method "^require(_relative)?$")) @require_call
            """;
        var matches = client.ExecuteQuery(requireQuery, source);

        matches.Should().NotBeEmpty();
        matches.SelectMany(m => m.Captures).Should().Contain(c => c.Name == "path" && c.Text == "json");
        matches.SelectMany(m => m.Captures).All(c => c.ByteRange.EndByte > c.ByteRange.StartByte).Should().BeTrue();
    }

    private static string ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
