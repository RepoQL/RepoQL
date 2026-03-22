using AwesomeAssertions;

namespace RepoQL.Data.DuckDB.Tests;

public class StructureOnlyFilterTests
{
    [Test]
    [DisplayName("Null URI returns false")]
    public void Given_NullUri_When_IsStructureOnly_Then_ReturnsFalse()
    {
        StructureOnlyFilter.IsStructureOnly(null).Should().BeFalse();
    }

    [Test]
    [DisplayName("Empty URI returns false")]
    public void Given_EmptyUri_When_IsStructureOnly_Then_ReturnsFalse()
    {
        StructureOnlyFilter.IsStructureOnly("").Should().BeFalse();
    }

    [Test]
    [Arguments("file:///C:/project/src/main.cs")]
    [Arguments("file:///home/user/project/src/app.ts")]
    [Arguments("file:///project/wwwroot/app.js")]
    [DisplayName("Normal source files are not structure-only")]
    public void Given_NormalSourceFile_When_IsStructureOnly_Then_ReturnsFalse(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeFalse();
    }

    [Test]
    [Arguments("file:///C:/project/wwwroot/lib/bootstrap/bootstrap.js")]
    [Arguments("file:///project/wwwroot/lib/jquery/jquery.js")]
    [Arguments("file:///home/user/app/wwwroot/lib/popper/popper.js")]
    [DisplayName("Files in wwwroot/lib are structure-only")]
    public void Given_WwwrootLibFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/vendor/lodash/lodash.js")]
    [Arguments("file:///project/vendor/moment/moment.js")]
    [DisplayName("Files in vendor directory are structure-only")]
    public void Given_VendorFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/assets/app.min.js")]
    [Arguments("file:///project/dist/bundle.min.js")]
    [Arguments("file:///home/user/project/public/styles.min.css")]
    [DisplayName("Minified files are structure-only")]
    public void Given_MinifiedFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/package-lock.json")]
    [Arguments("file:///project/yarn.lock")]
    [Arguments("file:///home/user/project/pnpm-lock.yaml")]
    [DisplayName("Lock files are structure-only")]
    public void Given_LockFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/node_modules/express/index.js")]
    [Arguments("file:///project/node_modules/@types/node/index.d.ts")]
    [DisplayName("Files in node_modules are structure-only")]
    public void Given_NodeModulesFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/src/Services/MyService.generated.cs")]
    [Arguments("file:///project/Models/Entity.g.cs")]
    [Arguments("file:///home/user/project/Views/Index.Designer.cs")]
    [DisplayName("Generated C# files are structure-only")]
    public void Given_GeneratedCSharpFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }

    [Test]
    [Arguments("file:///C:/project/dist/bundle.js")]
    [Arguments("file:///project/build/output.css")]
    [DisplayName("Files in dist/build directories are structure-only")]
    public void Given_DistBuildFile_When_IsStructureOnly_Then_ReturnsTrue(string uri)
    {
        StructureOnlyFilter.IsStructureOnly(uri).Should().BeTrue();
    }
}
