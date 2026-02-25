using AwesomeAssertions;
using RepoQL.Formats.Rust.TreeSitter;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustTreeSitterClientTests
{
    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new RustTreeSitterClient();
        var action = () => client.Parse(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_Empty_ReturnsEmptySurface()
    {
        using var client = new RustTreeSitterClient();

        var result = client.Parse(string.Empty);

        result.Structs.Should().BeEmpty();
        result.Enums.Should().BeEmpty();
        result.Traits.Should().BeEmpty();
        result.ImplBlocks.Should().BeEmpty();
        result.Functions.Should().BeEmpty();
        result.Modules.Should().BeEmpty();
        result.ErrorNodeCount.Should().Be(0);
    }

    [Test]
    public void Parse_SimpleStruct_ExtractsStructFieldsDerivesAndDocs()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("simple_struct.rs");

        var result = client.Parse(source);

        var user = result.Structs.Single(s => s.Name == "User");
        user.Visibility.Should().Be("public");
        user.Derives.Should().Contain("Debug");
        user.Attributes.Select(a => a.Name).Should().Contain(["derive", "repr"]);
        user.Fields.Select(f => f.Name).Should().Contain(["id", "value"]);
        user.Fields.Single(f => f.Name == "id").Visibility.Should().Be("public");
        user.Fields.Single(f => f.Name == "value").Visibility.Should().Be("private");
        user.DocComment.Should().Contain("A user in the system.");
    }

    [Test]
    public void Parse_EnumWithVariants_ExtractsVariantKinds()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("enum_with_variants.rs");

        var result = client.Parse(source);

        var enumInfo = result.Enums.Single(e => e.Name == "Message");
        enumInfo.Variants.Should().Contain(v => v.Name == "Quit" && v.VariantKind == "unit");
        enumInfo.Variants.Should().Contain(v => v.Name == "Write" && v.VariantKind == "tuple");
        enumInfo.Variants.Should().Contain(v => v.Name == "Move" && v.VariantKind == "struct");
    }

    [Test]
    public void Parse_TraitDefinition_ExtractsMethodsAndAssociatedMembers()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("trait_definition.rs");

        var result = client.Parse(source);

        var traitInfo = result.Traits.Single(t => t.Name == "Storage");
        traitInfo.Supertraits.Should().Contain("Send");
        traitInfo.Supertraits.Should().Contain("Sync");
        traitInfo.Methods.Select(m => m.Name).Should().Contain(["get", "set"]);
        traitInfo.AssociatedTypes.Should().ContainSingle(t => t.Name == "Item");
        traitInfo.AssociatedConsts.Should().ContainSingle(c => c.Name == "VERSION" && c.HasDefault);
    }

    [Test]
    public void Parse_ImplBlocks_ExtractsInherentAndTraitImpls()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("impl_blocks.rs");

        var result = client.Parse(source);

        result.ImplBlocks.Should().HaveCount(2);
        result.ImplBlocks.Should().Contain(i => i.TraitName == null && i.TargetType == "Cache");
        result.ImplBlocks.Should().Contain(i => i.TraitName == "Storage" && i.TargetType == "Cache");

        var inherent = result.ImplBlocks.Single(i => i.TraitName == null);
        inherent.Methods.Should().Contain(m => m.Name == "new" && m.SelfKind == "none");
        inherent.Methods.Should().Contain(m => m.Name == "refresh" && m.IsAsync && m.SelfKind == "&mut self");

        var traitImpl = result.ImplBlocks.Single(i => i.TraitName == "Storage");
        traitImpl.AssociatedTypes.Should().ContainSingle(t => t.Name == "Item");
        traitImpl.AssociatedConsts.Should().ContainSingle(c => c.Name == "VERSION");
    }

    [Test]
    public void Parse_VisibilityModifiers_ExtractsAllVisibilityForms()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("visibility_modifiers.rs");

        var result = client.Parse(source);

        result.Structs.Should().Contain(s => s.Name == "PublicType" && s.Visibility == "public");
        result.Structs.Should().Contain(s => s.Name == "CrateType" && s.Visibility == "pub_crate");
        result.Structs.Should().Contain(s => s.Name == "SuperType" && s.Visibility == "pub_super");
        result.Structs.Should().Contain(s => s.Name == "PathType" && s.Visibility == "pub_in:crate::outer");
        result.Structs.Should().Contain(s => s.Name == "PrivateType" && s.Visibility == "private");
    }

    [Test]
    public void Parse_UseDeclarations_ExtractsAliasesGlobAndPubUse()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("use_declarations.rs");

        var result = client.Parse(source);

        result.UseDeclarations.Should().Contain(u => u.Path == "std::fmt" && !u.IsGlob && !u.IsPub);
        result.UseDeclarations.Should().Contain(u => u.Path.Contains("HashMap") && u.Alias == "Map");
        result.UseDeclarations.Should().Contain(u => u.Path.Contains('*') && u.IsGlob);
        result.UseDeclarations.Should().Contain(u => u.IsPub && u.Path.Contains("crate::models"));
    }

    [Test]
    public void Parse_MacroDefinitions_ExtractsDefinitionsAndInvocations()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("macro_definitions.rs");

        var result = client.Parse(source);

        result.MacroDefs.Should().ContainSingle(m => m.Name == "make_value");
        result.MacroInvocations.Select(m => m.MacroName).Should().Contain(["make_value", "println"]);
    }

    [Test]
    public void Parse_AsyncFunctions_ExtractsAsyncFreeFunctionsAndMethods()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("async_functions.rs");

        var result = client.Parse(source);

        result.Functions.Should().ContainSingle(f => f.Name == "fetch_data" && f.IsAsync);

        var implBlock = result.ImplBlocks.Single(i => i.TargetType == "Worker");
        implBlock.Methods.Should().ContainSingle(m => m.Name == "run" && m.IsAsync && m.SelfKind == "&self");
    }

    [Test]
    public void Parse_GenericsAndLifetimes_ExtractsGenericsAndWhereClauses()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("generics_and_lifetimes.rs");

        var result = client.Parse(source);

        var wrapper = result.Structs.Single(s => s.Name == "Wrapper");
        wrapper.Generics.Should().Contain("'a");
        wrapper.WhereClause.Should().Contain("T: Clone");

        var build = result.Functions.Single(f => f.Name == "build");
        build.Generics.Should().Contain("'a");
        build.Parameters.Should().Contain("value");
    }

    [Test]
    public void Parse_ModulesConstantsStaticsTypeAliasesUnionsAttributesAndExternBlocks_ExtractsAll()
    {
        using var client = new RustTreeSitterClient();

        const string source = """
            #[derive(Debug)]
            pub union Token {
                pub i: i32,
                pub f: f32,
            }

            pub mod inner {
                pub const ANSWER: i32 = 42;
                static mut COUNTER: usize = 0;
                pub type Id = u64;
            }

            extern \"C\" {
                fn puts(s: *const i8) -> i32;
            }
            """;

        var result = client.Parse(source);

        result.Modules.Should().ContainSingle(m => m.Name == "inner" && m.Visibility == "public" && m.IsInline);
        result.Constants.Should().ContainSingle(c => c.Name == "ANSWER");
        result.Statics.Should().ContainSingle(s => s.Name == "COUNTER" && s.IsMutable);
        result.TypeAliases.Should().ContainSingle(t => t.Name == "Id");
        result.Unions.Should().ContainSingle(u => u.Name == "Token" && u.Derives!.Contains("Debug", StringComparison.Ordinal));
        result.Attributes.Should().Contain(a => a.Name == "derive");
        result.ExternBlocks.Should().ContainSingle();
        result.ExternBlocks.Single().Functions.Should().ContainSingle(f => f.Name == "puts");
    }

    [Test]
    public void Parse_Malformed_ReturnsPartialResultsAndErrorCount()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("malformed.rs");

        var result = client.Parse(source);

        result.ErrorNodeCount.Should().BeGreaterThan(0);
        result.Structs.Should().Contain(s => s.Name == "Recovered");
    }

    [Test]
    public void ExecuteQuery_ReturnsCaptureGroupsWithByteRanges()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("use_declarations.rs");

        var groups = client.ExecuteQuery(source, RustQueries.UseDeclarations);

        groups.Should().NotBeEmpty();
        groups.SelectMany(g => g.Captures).Should().Contain(c => c.Name == "path" && c.Text.Contains("std::fmt", StringComparison.Ordinal));
        groups.SelectMany(g => g.Captures).All(c => c.ByteRange.EndByte > c.ByteRange.StartByte).Should().BeTrue();
    }
}
