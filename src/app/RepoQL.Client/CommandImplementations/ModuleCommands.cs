using System.Text;
using RepoQL.Commands;
using RepoQL.Sandbox;

namespace RepoQL.Client.CommandImplementations;

/// <summary>
/// Purpose: Manage registered sandbox JavaScript modules stored under .repoql/modules/.
/// Complexity: Thin command layer over IModuleRegistry with compact table/status rendering.
/// </summary>
[CommandClass]
internal sealed class ModuleCommands(IModuleRegistry moduleRegistry)
{
    [Command("module.register", Description = "Register a sandbox module from .repoql/modules/src")]
    public CommandResult Register(
        [CommandParam("Module identifier (e.g. @agent/mymodule)")] string identifier)
    {
        var requestedIdentifier = identifier.Trim();
        var result = moduleRegistry.Register(identifier);
        if (!result.Success)
            return CommandResult.Error(string.Join(Environment.NewLine, result.Errors));

        var module = moduleRegistry.List()
            .FirstOrDefault(entry => string.Equals(entry.Identifier, requestedIdentifier, StringComparison.OrdinalIgnoreCase));
        if (module is null)
            return CommandResult.Success($"Registered {requestedIdentifier}");

        var sb = new StringBuilder();
        sb.AppendLine($"Registered {module.Identifier}");
        sb.AppendLine($"  Specifier:    {module.Specifier}");
        sb.AppendLine($"  Capabilities: {module.Capabilities}");
        sb.Append($"  Source hash:  {module.SourceHash}");

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
                sb.AppendLine($"  - {warning}");
        }

        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    [Command("module.remove", Description = "Remove a sandbox module registration")]
    public CommandResult Remove(
        [CommandParam("Module identifier (e.g. @agent/mymodule)")] string identifier)
        => moduleRegistry.Remove(identifier)
            ? CommandResult.Success($"Removed {identifier}")
            : CommandResult.Success($"Module not found: {identifier}");

    [Command("module.list", Description = "List registered sandbox modules")]
    public CommandResult List()
    {
        var modules = moduleRegistry.List()
            .OrderBy(module => module.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modules.Count == 0)
            return CommandResult.Success("No modules registered.");

        var rows = modules.Select(module => new
        {
            module.Identifier,
            Capabilities = module.Capabilities.ToString(),
            Health = module.IsHealthy ? "healthy" : "unhealthy",
            Registered = module.RegisteredAt.ToString("u")
        }).ToList();

        var idWidth = Math.Max("identifier".Length, rows.Max(row => row.Identifier.Length));
        var capWidth = Math.Max("capabilities".Length, rows.Max(row => row.Capabilities.Length));
        var healthWidth = Math.Max("health".Length, rows.Max(row => row.Health.Length));

        var sb = new StringBuilder();
        sb.AppendLine($"{Pad("identifier", idWidth)}  {Pad("capabilities", capWidth)}  {Pad("health", healthWidth)}  registered");
        foreach (var row in rows)
        {
            sb.AppendLine($"{Pad(row.Identifier, idWidth)}  {Pad(row.Capabilities, capWidth)}  {Pad(row.Health, healthWidth)}  {row.Registered}");
        }

        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    [Command("module.check", Description = "Health-check registered sandbox modules")]
    public CommandResult Check()
    {
        var results = moduleRegistry.CheckHealth()
            .OrderBy(result => result.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (results.Count == 0)
            return CommandResult.Success("No modules registered.");

        var sb = new StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine(result.IsHealthy
                ? $"{result.Identifier}: healthy"
                : $"{result.Identifier}: {result.Problem}");
        }

        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    private static string Pad(string value, int width) => value.PadRight(width);
}
