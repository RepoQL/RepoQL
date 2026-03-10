using AwesomeAssertions;
using RepoQL.Templating;

namespace RepoQL.Formats.DotNet.Tests;

internal class LiquidTemplateRendererTests
{
    [Test]
    public async Task Render_MatchesAsyncRender_ForDictionaryModel()
    {
        var renderer = new LiquidTemplateRenderer(
            typeof(AppSettingsLoader).Assembly,
            "RepoQL.Formats.DotNet.Templates");

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = "appsettings.json",
            ["environment"] = "Production",
            ["media_kind"] = "config.appsettings",
            ["size_bytes"] = 1536,
            ["line_count"] = 18,
            ["top_keys"] = new[] { "ConnectionStrings", "Logging" },
            ["connection_strings"] = new[] { "Default", "Cache" },
            ["services"] = new[] { "SqlServer", "Redis" }
        };

        var syncRendered = renderer.Render("explore/headline-appsettings", model);
        var asyncRendered = await renderer.RenderAsync("explore/headline-appsettings", model);

        syncRendered.Should().Be(asyncRendered);
        syncRendered.Should().Contain("appsettings.json");
        syncRendered.Should().Contain("config.appsettings");
        syncRendered.Should().Contain("cs:Default,Cache");
        syncRendered.Should().Contain("SqlServer, Redis");
    }
}
