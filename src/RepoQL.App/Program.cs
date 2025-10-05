using Spectre.Console.Cli;

// Entry
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("repoql");
    // Top-level commands (accept trailing arguments directly)
    config.AddCommand<XrayCommand>("xray");
    config.AddCommand<QueryCommand>("query");

    // Namespaced commands
    config.AddBranch("host", host => host.AddCommand<HostServeCommand>("serve"));
    config.AddBranch("mcp", mcp => mcp.AddCommand<McpServeCommand>("serve"));
});

return app.Run(args);