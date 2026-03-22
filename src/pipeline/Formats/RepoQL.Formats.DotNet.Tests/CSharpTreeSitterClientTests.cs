using AwesomeAssertions;
using RepoQL.Formats.DotNet.TreeSitter;

namespace RepoQL.Formats.DotNet.Tests;

internal sealed class CSharpTreeSitterClientTests
{
    private static readonly Guid TestDocumentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Test]
    public void Parse_Empty_ReturnsEmptySurface()
    {
        using var client = new CSharpTreeSitterClient();
        var result = client.Parse(TestDocumentId, string.Empty);

        result.DocumentId.Should().Be(TestDocumentId);
        result.Namespaces.Should().BeEmpty();
        result.Types.Should().BeEmpty();
        result.Members.Should().BeEmpty();
        result.Usings.Should().BeEmpty();
    }

    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new CSharpTreeSitterClient();
        var action = () => client.Parse(TestDocumentId, null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_Usings_ExtractsRegularStaticAndAliased()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            using System;
            using static System.Math;
            using Alias = System.Collections.Generic;
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Usings.Should().HaveCount(3);
        result.Usings.Should().Contain(u => u.Name == "System" && !u.IsStatic && u.Alias == null);
        result.Usings.Should().Contain(u => u.Name == "System.Math" && u.IsStatic);
        result.Usings.Should().Contain(u => u.Alias == "Alias");
    }

    [Test]
    public void Parse_Namespace_ExtractsBlockScoped()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            namespace Foo.Bar
            {
                public class Baz { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Namespaces.Should().ContainSingle();
        result.Namespaces[0].Name.Should().Be("Foo.Bar");
        result.Namespaces[0].QualifiedName.Should().Be("Foo.Bar");
        result.Namespaces[0].ParentNamespaceId.Should().BeNull();
    }

    [Test]
    public void Parse_Namespace_ExtractsFileScoped()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            namespace Example.Test;

            public class Foo { }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Namespaces.Should().ContainSingle();
        result.Namespaces[0].QualifiedName.Should().Be("Example.Test");
    }

    [Test]
    public void Parse_Class_ExtractsNameAccessibilityAndModifiers()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            namespace Example;

            public sealed partial class Foo : Base, IBar
            {
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().ContainSingle();
        var type = result.Types[0];
        type.Name.Should().Be("Foo");
        type.Kind.Should().Be("class");
        type.Accessibility.Should().Be("public");
        type.IsPartial.Should().BeTrue();
        type.Modifiers.Should().Contain("sealed");
        type.Modifiers.Should().Contain("partial");
        type.BaseType.Should().Be("Base");
        type.Interfaces.Should().Contain("IBar");
        type.Namespace.Should().Be("Example");
        type.QualifiedName.Should().Be("Example.Foo");
    }

    [Test]
    public void Parse_Struct_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public readonly struct Point
            {
                public int X { get; }
                public int Y { get; }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().ContainSingle(t => t.Kind == "struct");
        var type = result.Types[0];
        type.Name.Should().Be("Point");
        type.Modifiers.Should().Contain("readonly");
    }

    [Test]
    public void Parse_Record_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public record Person(string Name, int Age);
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().ContainSingle(t => t.Kind == "record");
        var type = result.Types[0];
        type.Name.Should().Be("Person");
        type.IsRecord.Should().BeTrue();
    }

    [Test]
    public void Parse_Interface_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public interface IFoo : IBar, IBaz
            {
                void DoSomething();
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().ContainSingle(t => t.Kind == "interface");
        var type = result.Types[0];
        type.Name.Should().Be("IFoo");
        type.BaseType.Should().BeNull();
        type.Interfaces.Should().BeEquivalentTo(["IBar", "IBaz"]);
    }

    [Test]
    public void Parse_Enum_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public enum Color
            {
                Red, Green, Blue
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().ContainSingle(t => t.Kind == "enum");
        result.Types[0].Name.Should().Be("Color");
    }

    [Test]
    public void Parse_Method_ExtractsNameReturnTypeAndParameters()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public async Task<int> Calculate(string input, int count = 0)
                {
                    return 42;
                }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "method");
        var method = result.Members.Single(m => m.Kind == "method");
        method.Name.Should().Be("Calculate");
        method.ReturnType.Should().Be("Task<int>");
        method.IsAsync.Should().BeTrue();
        method.Accessibility.Should().Be("public");
        method.Parameters.Should().HaveCount(2);
        method.Parameters[0].Name.Should().Be("input");
        method.Parameters[0].Type.Should().Be("string");
        method.Parameters[1].Name.Should().Be("count");
        method.Parameters[1].HasDefaultValue.Should().BeTrue();
    }

    [Test]
    public void Parse_Constructor_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public Foo(string name) { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "constructor");
        var ctor = result.Members.Single(m => m.Kind == "constructor");
        ctor.Name.Should().Be("Foo");
        ctor.Parameters.Should().ContainSingle(p => p.Name == "name" && p.Type == "string");
    }

    [Test]
    public void Parse_Property_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public string Name { get; set; }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "property");
        var prop = result.Members.Single(m => m.Kind == "property");
        prop.Name.Should().Be("Name");
        prop.ReturnType.Should().Be("string");
        prop.Accessibility.Should().Be("public");
    }

    [Test]
    public void Parse_Field_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                private readonly string _name;
                private static int _count;
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        var fields = result.Members.Where(m => m.Kind == "field").ToList();
        fields.Should().HaveCount(2);
        fields.Should().Contain(f => f.Name == "_name" && f.ReturnType == "string" && !f.IsStatic);
        fields.Should().Contain(f => f.Name == "_count" && f.ReturnType == "int" && f.IsStatic);
    }

    [Test]
    public void Parse_Event_ExtractsPropertyStyleEvent()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public event EventHandler<int> Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "event");
        var evt = result.Members.Single(m => m.Kind == "event");
        evt.Name.Should().Be("Changed");
    }

    [Test]
    public void Parse_EventField_ExtractsFieldStyleEvent()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public event EventHandler Changed;
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "event");
        var evt = result.Members.Single(m => m.Kind == "event");
        evt.Name.Should().Be("Changed");
    }

    [Test]
    public void Parse_Indexer_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public string this[int index] => "value";
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Should().ContainSingle(m => m.Kind == "indexer");
        var indexer = result.Members.Single(m => m.Kind == "indexer");
        indexer.Name.Should().Be("this");
        indexer.ReturnType.Should().Be("string");
    }

    [Test]
    public void Parse_NestedType_SetsParentTypeId()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Outer
            {
                private class Inner { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types.Should().HaveCount(2);
        var outer = result.Types.Single(t => t.Name == "Outer");
        var inner = result.Types.Single(t => t.Name == "Inner");
        inner.ParentTypeId.Should().Be(outer.NodeId);
        inner.QualifiedName.Should().Be("Outer.Inner");
        inner.Accessibility.Should().Be("private");
    }

    [Test]
    public void Parse_DocComment_ExtractsSummary()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            /// <summary>
            /// This is a test class.
            /// </summary>
            public class Foo { }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types[0].Summary.Should().Be("This is a test class.");
    }

    [Test]
    public void Parse_Modifiers_ExtractsAllAccessibilities()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public void A() { }
                private void B() { }
                protected void C() { }
                internal void D() { }
                protected internal void E() { }
                void F() { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Members.Single(m => m.Name == "A").Accessibility.Should().Be("public");
        result.Members.Single(m => m.Name == "B").Accessibility.Should().Be("private");
        result.Members.Single(m => m.Name == "C").Accessibility.Should().Be("protected");
        result.Members.Single(m => m.Name == "D").Accessibility.Should().Be("internal");
        result.Members.Single(m => m.Name == "E").Accessibility.Should().Be("protected internal");
        result.Members.Single(m => m.Name == "F").Accessibility.Should().Be("private");
    }

    [Test]
    public void Parse_MalformedCode_ReturnsPartialResults()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public void Valid() { }
                public voi Broken(
            }
            public class AlsoValid { }
            """;

        var result = client.Parse(TestDocumentId, source);

        // Error tolerance: should extract valid declarations despite syntax errors.
        result.Types.Should().Contain(t => t.Name == "Foo");
        result.Types.Should().Contain(t => t.Name == "AlsoValid");
        result.Members.Should().Contain(m => m.Name == "Valid");
    }

    [Test]
    public void Parse_MembersHaveDeclaringTypeId()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public class Foo
            {
                public void DoSomething() { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        var type = result.Types.Single();
        var member = result.Members.Single();
        member.DeclaringTypeId.Should().Be(type.NodeId);
        member.DeclaringTypeDisplay.Should().Be("Foo");
    }

    [Test]
    public async Task Concurrent_Parsing_IsThreadSafe()
    {
        using var client = new CSharpTreeSitterClient();
        var sources = Enumerable.Range(0, 8)
            .Select(i => $"public class C{i} {{ public void M{i}() {{ }} }}")
            .ToArray();

        var tasks = sources.Select(s => Task.Run(() => client.Parse(TestDocumentId, s))).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(8);
        for (var i = 0; i < 8; i++)
        {
            results[i].Types.Should().ContainSingle(t => t.Name == $"C{i}");
        }
    }

    [Test]
    public void Parse_StaticClass_ExtractsCorrectly()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public static class Utils
            {
                public static int Add(int a, int b) => a + b;
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types[0].IsStatic.Should().BeTrue();
        result.Types[0].Modifiers.Should().Contain("static");
        result.Members[0].IsStatic.Should().BeTrue();
    }

    [Test]
    public void Parse_AbstractClass_ExtractsVirtualAndAbstractMembers()
    {
        using var client = new CSharpTreeSitterClient();
        var source = """
            public abstract class Base
            {
                public abstract void DoSomething();
                public virtual void DoOther() { }
            }
            """;

        var result = client.Parse(TestDocumentId, source);

        result.Types[0].Modifiers.Should().Contain("abstract");
        result.Members.Single(m => m.Name == "DoSomething").Modifiers.Should().Contain("abstract");
        result.Members.Single(m => m.Name == "DoOther").Modifiers.Should().Contain("virtual");
    }
}
