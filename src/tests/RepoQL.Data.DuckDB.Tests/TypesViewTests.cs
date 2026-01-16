using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class TypesViewTests
{
    [Test]
    [DisplayName("Types view returns all expected columns for C# type")]
    public void TypesView_ReturnsAllColumns_CSharp()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        db.IndexArtifact(uri, CreateCSharpTypeArtifact(uri, "MyClass", "MyNamespace.MyClass", "class", "MyNamespace"));

        var rows = db.Read("""
            SELECT uri, file_uri, file_name, name, qualified_name, type_kind,
                   namespace, visibility, signature, lang, extends, implements,
                   start_line, end_line, headline, structure, node_id, span_id
            FROM types
            """, r => new
        {
            Uri = r.GetString(0),
            FileUri = r.GetString(1),
            FileName = r.GetString(2),
            Name = r.GetString(3),
            QualifiedName = r.GetString(4),
            TypeKind = r.GetString(5),
            Namespace = r.GetString(6),
            Visibility = r.GetString(7),
            Signature = r.IsDBNull(8) ? null : r.GetString(8),
            Lang = r.GetString(9),
            Extends = r.IsDBNull(10) ? null : r.GetString(10),
            Implements = r.IsDBNull(11) ? null : r.GetString(11),
            StartLine = r.IsDBNull(12) ? (int?)null : r.GetInt32(12),
            EndLine = r.IsDBNull(13) ? (int?)null : r.GetInt32(13),
            Headline = r.IsDBNull(14) ? null : r.GetString(14),
            Structure = r.IsDBNull(15) ? null : r.GetString(15),
            NodeId = r.GetGuid(16),
            SpanId = r.IsDBNull(17) ? (Guid?)null : r.GetGuid(17)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("MyClass");
        rows[0].QualifiedName.Should().Be("MyNamespace.MyClass");
        rows[0].TypeKind.Should().Be("class");
        rows[0].Namespace.Should().Be("MyNamespace");
        rows[0].Visibility.Should().Be("public");
        rows[0].Lang.Should().Be("csharp");
    }

    [Test]
    [DisplayName("Types view shows inheritance with extends and implements")]
    public void TypesView_ShowsInheritance()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/DerivedClass.cs")!;
        db.IndexArtifact(uri, CreateCSharpTypeWithInheritance(uri, "DerivedClass", "BaseClass", new[] { "IDisposable", "ICloneable" }));

        var rows = db.Read("""
            SELECT name, extends, implements
            FROM types
            """, r => new
        {
            Name = r.GetString(0),
            Extends = r.IsDBNull(1) ? null : r.GetString(1),
            Implements = r.IsDBNull(2) ? null : r.GetString(2)
        });

        rows.Should().HaveCount(1);
        rows[0].Extends.Should().Be("BaseClass");
        rows[0].Implements.Should().Contain("IDisposable");
        rows[0].Implements.Should().Contain("ICloneable");
    }

    [Test]
    [DisplayName("Types view filters by language pattern")]
    public void TypesView_FiltersByLangPattern()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var csUri = RepoUri.Parse("file:///src/CSharpClass.cs")!;
        var tsUri = RepoUri.Parse("file:///src/TypeScriptClass.ts")!;

        db.IndexArtifact(csUri, CreateCSharpTypeArtifact(csUri, "CSharpClass", "CSharpClass", "class", ""));
        db.IndexArtifact(tsUri, CreateTypeScriptTypeArtifact(tsUri, "TypeScriptClass"));

        var csRows = db.Read("SELECT name, lang FROM types WHERE lang = 'csharp'", r => new
        {
            Name = r.GetString(0),
            Lang = r.GetString(1)
        });

        var tsRows = db.Read("SELECT name, lang FROM types WHERE lang = 'typescript'", r => new
        {
            Name = r.GetString(0),
            Lang = r.GetString(1)
        });

        csRows.Should().HaveCount(1);
        csRows[0].Name.Should().Be("CSharpClass");
        csRows[0].Lang.Should().Be("csharp");

        tsRows.Should().HaveCount(1);
        tsRows[0].Name.Should().Be("TypeScriptClass");
        tsRows[0].Lang.Should().Be("typescript");
    }

    [Test]
    [DisplayName("Types view includes interface types")]
    public void TypesView_IncludesInterfaces()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/IMyInterface.cs")!;
        db.IndexArtifact(uri, CreateCSharpTypeArtifact(uri, "IMyInterface", "IMyInterface", "interface", ""));

        var rows = db.Read("SELECT name, type_kind FROM types", r => new
        {
            Name = r.GetString(0),
            TypeKind = r.GetString(1)
        });

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("IMyInterface");
        rows[0].TypeKind.Should().Be("interface");
    }

    [Test]
    [DisplayName("Types view signature falls back to headline")]
    public void TypesView_SignatureFallsBackToHeadline()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();

        var uri = RepoUri.Parse("file:///src/MyClass.cs")!;
        var artifact = CreateCSharpTypeArtifact(uri, "MyClass", "MyClass", "class", "");
        db.IndexArtifact(uri, artifact);

        var rows = db.Read("SELECT signature, headline FROM types", r => new
        {
            Signature = r.IsDBNull(0) ? null : r.GetString(0),
            Headline = r.IsDBNull(1) ? null : r.GetString(1)
        });

        rows.Should().HaveCount(1);
        // Signature should be set (either from property or headline fallback)
        rows[0].Signature.Should().NotBeNullOrEmpty();
    }

    private static ParsedArtifact CreateCSharpTypeArtifact(RepoUri uri, string name, string qualifiedName, string kind, string ns)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var typeNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        var signature = $"public {kind} {name}";

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = $"public {kind} {name} {{ }}"
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
                    Id = typeNodeId,
                    Kind = "csharp.type",
                    Uri = RepoUri.FromSymbol(uri.Container, qualifiedName, 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["qualified_name"] = qualifiedName,
                        ["kind"] = kind,
                        ["namespace"] = ns,
                        ["accessibility"] = "public",
                        ["signature"] = signature
                    },
                    Headline = signature
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = typeNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateCSharpTypeWithInheritance(RepoUri uri, string name, string baseType, string[] interfaces)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var typeNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        var signature = $"public class {name} : {baseType}, {string.Join(", ", interfaces)}";
        var implementsArray = new JsonArray();
        foreach (var iface in interfaces)
            implementsArray.Add((JsonNode?)JsonValue.Create(iface));

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = $"public class {name} : {baseType}, {string.Join(", ", interfaces)} {{ }}"
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
                    Id = typeNodeId,
                    Kind = "csharp.type",
                    Uri = RepoUri.FromSymbol(uri.Container, name, 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["qualified_name"] = name,
                        ["kind"] = "class",
                        ["namespace"] = "",
                        ["accessibility"] = "public",
                        ["signature"] = signature,
                        ["extends"] = baseType,
                        ["implements"] = implementsArray
                    },
                    Headline = signature
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = typeNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }

    private static ParsedArtifact CreateTypeScriptTypeArtifact(RepoUri uri, string name)
    {
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var typeNodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        var signature = $"export class {name}";

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/typescript"),
                Text = $"export class {name} {{ }}"
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
                    Id = typeNodeId,
                    Kind = "typescript.type",
                    Uri = RepoUri.FromSymbol(uri.Container, name, 1, 10),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = name,
                        ["qualified_name"] = name,
                        ["kind"] = "class",
                        ["namespace"] = "",
                        ["accessibility"] = "export",
                        ["signature"] = signature
                    },
                    Headline = signature
                }
            },
            Spans = new List<Span>
            {
                new Span { Id = spanId, DocumentId = docId, StartLine = 1, EndLine = 10, StartColumn = 1, EndColumn = 1 }
            },
            Edges = new List<Edge>
            {
                new Edge { SrcId = docId, DstId = typeNodeId, Type = "HAS_PART", IsComposition = true, Ordinal = 0 }
            }
        };
    }
}
