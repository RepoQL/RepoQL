using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class FunctionsViewTests
{
    [Test]
    [DisplayName("Functions view returns all expected columns for C# method")]
    public void FunctionsView_ReturnsAllColumns_CSharpMethod()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        db.IndexArtifact(uri, CreateCSharpMethodArtifact(uri, "MyMethod", "MyClass", "method", isStatic: false, isAsync: true));

        var rows = db.Read("""
            SELECT uri, file_uri, file_name, name, qualified_name, function_kind,
                   declaring_type, visibility, signature, return_type, lang,
                   is_static, is_async, start_line, end_line, headline, node_id, span_id
            FROM functions
            """, r => new
        {
            Uri = r.GetString(0),
            FileUri = r.GetString(1),
            FileName = r.GetString(2),
            Name = r.GetString(3),
            QualifiedName = r.GetString(4),
            FunctionKind = r.GetString(5),
            DeclaringType = r.IsDBNull(6) ? null : r.GetString(6),
            Visibility = r.GetString(7),
            Signature = r.IsDBNull(8) ? null : r.GetString(8),
            ReturnType = r.IsDBNull(9) ? null : r.GetString(9),
            Lang = r.GetString(10),
            IsStatic = r.GetBoolean(11),
            IsAsync = r.GetBoolean(12),
            StartLine = r.IsDBNull(13) ? (int?)null : r.GetInt32(13),
            EndLine = r.IsDBNull(14) ? (int?)null : r.GetInt32(14),
            Headline = r.IsDBNull(15) ? null : r.GetString(15),
            NodeId = r.GetGuid(16),
            SpanId = r.IsDBNull(17) ? (Guid?)null : r.GetGuid(17)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("MyMethod");
        rows[0].QualifiedName.Should().Be("MyClass.MyMethod");
        rows[0].FunctionKind.Should().Be("method");
        rows[0].DeclaringType.Should().Be("MyClass");
        rows[0].Visibility.Should().Be("public");
        rows[0].Lang.Should().Be("csharp");
        rows[0].IsStatic.Should().BeFalse();
        rows[0].IsAsync.Should().BeTrue();
    }

    [Test]
    [DisplayName("Functions view excludes properties")]
    public void FunctionsView_ExcludesProperties()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;

        // Add a method and a property
        db.IndexArtifact(uri, CreateCSharpMethodArtifact(uri, "MyMethod", "MyClass", "method", isStatic: false, isAsync: false));

        var propUri = RepoUri.Parse("file:///src/MyClass2.cs")!;
        db.IndexArtifact(propUri, CreateCSharpMemberArtifact(propUri, "MyProperty", "MyClass", "property"));

        var rows = db.Read("SELECT name, function_kind FROM functions", r => new
        {
            Name = r.GetString(0),
            Kind = r.GetString(1)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("MyMethod");
        rows[0].Kind.Should().Be("method");
    }

    [Test]
    [DisplayName("Functions view includes constructors")]
    public void FunctionsView_IncludesConstructors()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        db.IndexArtifact(uri, CreateCSharpMethodArtifact(uri, ".ctor", "MyClass", "constructor", isStatic: false, isAsync: false));

        var rows = db.Read("SELECT name, function_kind FROM functions", r => new
        {
            Name = r.GetString(0),
            Kind = r.GetString(1)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be(".ctor");
        rows[0].Kind.Should().Be("constructor");
    }

    [Test]
    [DisplayName("Functions view includes static methods")]
    public void FunctionsView_IncludesStaticMethods()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        db.IndexArtifact(uri, CreateCSharpMethodArtifact(uri, "StaticMethod", "MyClass", "method", isStatic: true, isAsync: false));

        var rows = db.Read("SELECT name, is_static FROM functions", r => new
        {
            Name = r.GetString(0),
            IsStatic = r.GetBoolean(1)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("StaticMethod");
        rows[0].IsStatic.Should().BeTrue();
    }

    [Test]
    [DisplayName("Functions view includes TypeScript methods")]
    public void FunctionsView_IncludesTypeScriptMethods()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.ts")!;
        db.IndexArtifact(uri, CreateTypeScriptMethodArtifact(uri, "myMethod", "MyClass"));

        var rows = db.Read("SELECT name, lang, function_kind FROM functions", r => new
        {
            Name = r.GetString(0),
            Lang = r.GetString(1),
            Kind = r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("myMethod");
        rows[0].Lang.Should().Be("typescript");
    }

    [Test]
    [DisplayName("Functions view includes TypeScript standalone functions")]
    public void FunctionsView_IncludesTypeScriptFunctions()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/utils.ts")!;
        db.IndexArtifact(uri, CreateTypeScriptFunctionArtifact(uri, "helperFunction"));

        var rows = db.Read("SELECT name, function_kind, declaring_type FROM functions", r => new
        {
            Name = r.GetString(0),
            Kind = r.GetString(1),
            DeclaringType = r.IsDBNull(2) ? null : r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("helperFunction");
        rows[0].Kind.Should().Be("function");
        rows[0].DeclaringType.Should().BeNull();
    }

    [Test]
    [DisplayName("Functions view qualified_name fallback works")]
    public void FunctionsView_QualifiedNameFallback()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        // Create a method without qualified_name set - should fall back to declaring_type.name
        db.IndexArtifact(uri, CreateCSharpMethodWithoutQualifiedName(uri, "MyMethod", "MyClass"));

        var rows = db.Read("SELECT name, qualified_name, declaring_type FROM functions", r => new
        {
            Name = r.GetString(0),
            QualifiedName = r.GetString(1),
            DeclaringType = r.IsDBNull(2) ? null : r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].QualifiedName.Should().Be("MyClass.MyMethod");
    }

    private static ParsedArtifact CreateCSharpMethodArtifact(RepoUri uri, string name, string declaringType, string kind, bool isStatic, bool isAsync)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var memberNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        var signature = $"public {(isStatic ? "static " : "")}{(isAsync ? "async " : "")}void {name}()";

        var props = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["declaring_type"] = declaringType,
            ["qualified_name"] = $"{declaringType}.{name}",
            ["accessibility"] = "public",
            ["signature"] = signature,
            ["return_type"] = "void"
        };

        if (isStatic) props["is_static"] = true;
        if (isAsync) props["is_async"] = true;

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = $"public class {declaringType} {{ {signature} {{ }} }}"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = declaringType,
                Props = new JsonObject { ["title"] = declaringType }
            },
            Children = new List<Node>
            {
                new Node
                {
                    Id = memberNodeId,
                    Kind = "csharp.member",
                    Uri = RepoUri.FromSymbol(uri.Container, $"{declaringType}.{name}", 1, 10),
                    SpanId = spanId,
                    Props = props,
                    Headline = signature
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = memberNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateCSharpMemberArtifact(RepoUri uri, string name, string declaringType, string kind)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var memberNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = $"public class {declaringType} {{ public string {name} {{ get; set; }} }}"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = declaringType,
                Props = new JsonObject { ["title"] = declaringType }
            },
            Children = new List<Node>
            {
                new Node
                {
                    Id = memberNodeId,
                    Kind = "csharp.member",
                    Uri = RepoUri.FromSymbol(uri.Container, $"{declaringType}.{name}", 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["kind"] = kind,
                        ["declaring_type"] = declaringType,
                        ["accessibility"] = "public"
                    },
                    Headline = $"public string {name}"
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = memberNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateCSharpMethodWithoutQualifiedName(RepoUri uri, string name, string declaringType)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var memberNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = $"public class {declaringType} {{ public void {name}() {{ }} }}"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = declaringType,
                Props = new JsonObject { ["title"] = declaringType }
            },
            Children = new List<Node>
            {
                new Node
                {
                    Id = memberNodeId,
                    Kind = "csharp.member",
                    Uri = RepoUri.FromSymbol(uri.Container, $"{declaringType}.{name}", 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["kind"] = "method",
                        ["declaring_type"] = declaringType,
                        ["accessibility"] = "public"
                        // Note: no qualified_name - should fall back
                    },
                    Headline = $"public void {name}()"
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = memberNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateTypeScriptMethodArtifact(RepoUri uri, string name, string declaringType)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var memberNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/typescript"),
                Text = $"class {declaringType} {{ {name}() {{ }} }}"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = declaringType,
                Props = new JsonObject { ["title"] = declaringType }
            },
            Children = new List<Node>
            {
                new Node
                {
                    Id = memberNodeId,
                    Kind = "typescript.member",
                    Uri = RepoUri.FromSymbol(uri.Container, $"{declaringType}.{name}", 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["kind"] = "method",
                        ["declaring_type"] = declaringType
                    },
                    Headline = $"method {name}"
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = memberNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateTypeScriptFunctionArtifact(RepoUri uri, string name)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var funcNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/typescript"),
                Text = $"export function {name}() {{ }}"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = name,
                Props = new JsonObject { ["title"] = name }
            },
            Children = new List<Node>
            {
                new Node
                {
                    Id = funcNodeId,
                    Kind = "typescript.function",
                    Uri = RepoUri.FromSymbol(uri.Container, name, 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["kind"] = "function",
                        ["decl_kind"] = "function",
                        ["is_exported"] = true
                    },
                    Headline = $"export function {name}"
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = funcNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }
}
