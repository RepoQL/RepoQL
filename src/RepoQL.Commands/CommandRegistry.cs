using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Commands;

/// <summary>
/// Purpose: Discover, validate, and dispatch commands marked with framework attributes.
/// Complexity: Assembly scanning (like UdfRegistry), DI-based instantiation, prefix-aware
/// subcommand listing, Levenshtein fuzzy matching, and --help generation from metadata.
/// </summary>
public sealed class CommandRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CommandRegistry> _logger;
    private readonly Dictionary<string, CommandRegistration> _commands = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry(IServiceProvider serviceProvider, ILogger<CommandRegistry>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<CommandRegistry>.Instance;
    }

    /// <summary>
    /// Scan loaded assemblies for classes with [CommandClass] and register their [Command] methods.
    /// </summary>
    [RequiresUnreferencedCode("Command discovery uses reflection. Types are preserved via ILLink.Descriptors.xml.")]
    public void DiscoverCommands()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        _logger.LogDebug("[CommandRegistry] Scanning {Count} assemblies for command classes", assemblies.Count);

        var commandClasses = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.GetCustomAttribute<CommandClassAttribute>() != null)
            .ToList();

        _logger.LogInformation("[CommandRegistry] Found {Count} command classes", commandClasses.Count);

        foreach (var type in commandClasses)
        {
            RegisterCommandClass(type);
        }

        _logger.LogInformation("[CommandRegistry] Registered {Count} commands", _commands.Count);
    }

    private void RegisterCommandClass(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<CommandAttribute>();
            if (attr == null) continue;

            var allParams = method.GetParameters();
            var userParams = allParams.Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            var hasCancellation = allParams.Any(p => p.ParameterType == typeof(CancellationToken));

            var registration = new CommandRegistration(attr, type, method, userParams, hasCancellation);

            if (_commands.TryGetValue(attr.Name, out var existing))
            {
                _logger.LogWarning("[CommandRegistry] Duplicate command '{Name}' in {Type}, already registered from {ExistingType}",
                    attr.Name, type.Name, existing.ClassType.Name);
                continue;
            }

            _commands[attr.Name] = registration;
            _logger.LogDebug("[CommandRegistry] Registered command ::{Name} from {Type}.{Method}",
                attr.Name, type.Name, method.Name);
        }
    }

    /// <summary>
    /// Execute a parsed command. Handles help, exact match, prefix listing, and fuzzy suggestion.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(ParsedCommand parsed, CancellationToken cancel)
    {
        // Parse errors are reported immediately
        if (parsed.ParseError != null)
            return CommandResult.Error($"Command syntax error: {parsed.ParseError}");

        // Help on exact match → show usage
        if (parsed.IsHelp && _commands.TryGetValue(parsed.Name, out var helpCmd))
            return CommandResult.Success(GenerateUsage(helpCmd));

        // Help on prefix → list subcommands
        if (parsed.IsHelp)
        {
            var prefixHelp = ListSubcommands(parsed.Name);
            if (prefixHelp != null)
                return CommandResult.Success(prefixHelp);
            // Fall through to fuzzy match below
        }

        // Exact match → dispatch
        if (_commands.TryGetValue(parsed.Name, out var registration))
            return await DispatchAsync(registration, parsed, cancel);

        // Prefix match → list subcommands
        var subcommandList = ListSubcommands(parsed.Name);
        if (subcommandList != null)
            return CommandResult.Success(subcommandList);

        // No match → fuzzy suggest
        var suggestion = FindClosestCommand(parsed.Name);
        var message = suggestion != null
            ? $"Unknown command ::{parsed.Name}. Did you mean ::{suggestion}?"
            : $"Unknown command ::{parsed.Name}. No commands registered.";

        return CommandResult.Error(message);
    }

    private async Task<CommandResult> DispatchAsync(CommandRegistration registration, ParsedCommand parsed, CancellationToken cancel)
    {
        var userParams = registration.UserParameters;
        var requiredCount = CountRequiredParams(userParams);
        var totalCount = userParams.Length;

        if (parsed.Parameters.Length < requiredCount || parsed.Parameters.Length > totalCount)
        {
            return CommandResult.Error(GenerateUsage(registration, wrongParamCount: true,
                actual: parsed.Parameters.Length));
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, registration.ClassType);

            // Build argument array
            var allParams = registration.Method.GetParameters();
            var args = new object?[allParams.Length];
            var userIndex = 0;

            for (var i = 0; i < allParams.Length; i++)
            {
                if (allParams[i].ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancel;
                }
                else if (userIndex < parsed.Parameters.Length)
                {
                    args[i] = parsed.Parameters[userIndex++];
                }
                else
                {
                    // Honor declared default values; fall back to null for nullable params
                    args[i] = allParams[i].HasDefaultValue ? allParams[i].DefaultValue : null;
                }
            }

            var result = registration.Method.Invoke(instance, args);

            // Handle async methods
            if (result is Task<CommandResult> taskResult)
                return await taskResult;

            if (result is CommandResult syncResult)
                return syncResult;

            return CommandResult.Error($"Command ::{registration.Attribute.Name} returned unexpected type: {result?.GetType().Name ?? "null"}");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            _logger.LogError(ex.InnerException, "Command ::{Name} failed", registration.Attribute.Name);
            return CommandResult.Error($"Command ::{registration.Attribute.Name} failed: {ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command ::{Name} failed", registration.Attribute.Name);
            return CommandResult.Error($"Command ::{registration.Attribute.Name} failed: {ex.Message}");
        }
    }

    private static int CountRequiredParams(ParameterInfo[] userParams)
    {
        var nullabilityContext = new NullabilityInfoContext();
        var count = 0;

        foreach (var param in userParams)
        {
            var nullability = nullabilityContext.Create(param);
            var isOptional = param.HasDefaultValue
                             || nullability.WriteState == NullabilityState.Nullable
                             || Nullable.GetUnderlyingType(param.ParameterType) != null;

            if (!isOptional) count++;
        }

        return count;
    }

    private string GenerateUsage(CommandRegistration reg, bool wrongParamCount = false, int actual = 0)
    {
        var sb = new StringBuilder();

        if (wrongParamCount)
        {
            var required = CountRequiredParams(reg.UserParameters);
            sb.AppendLine($"Wrong parameter count for ::{reg.Attribute.Name}: got {actual}, expected {required}{(reg.UserParameters.Length > required ? $"-{reg.UserParameters.Length}" : "")}.");
            sb.AppendLine();
        }

        // Header
        sb.Append($"::{reg.Attribute.Name}");
        if (reg.Attribute.Description != null)
            sb.Append($" — {reg.Attribute.Description}");
        sb.AppendLine();

        // Usage line
        if (reg.UserParameters.Length > 0)
        {
            var paramNames = reg.UserParameters.Select(p =>
            {
                var nullabilityContext = new NullabilityInfoContext();
                var nullability = nullabilityContext.Create(p);
                var isOptional = p.HasDefaultValue
                                 || nullability.WriteState == NullabilityState.Nullable
                                 || Nullable.GetUnderlyingType(p.ParameterType) != null;
                return isOptional ? $"{p.Name}?" : p.Name!;
            });

            sb.AppendLine($"\n  Usage: ::{reg.Attribute.Name}[{string.Join(", ", paramNames)}]");

            foreach (var p in reg.UserParameters)
            {
                var desc = p.GetCustomAttribute<CommandParamAttribute>()?.Description;
                if (desc != null)
                    sb.AppendLine($"    {p.Name}  {desc}");
            }
        }
        else
        {
            sb.AppendLine($"\n  Usage: ::{reg.Attribute.Name}");
        }

        return sb.ToString().TrimEnd();
    }

    private string? ListSubcommands(string prefix)
    {
        var matches = _commands
            .Where(kv => kv.Key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Available subcommands:");

        var maxLen = matches.Max(m => m.Key.Length) + 4; // +4 for "::" prefix + padding
        foreach (var (key, reg) in matches)
        {
            var padded = $"::{key}".PadRight(maxLen + 2);
            var desc = reg.Attribute.Description ?? "";
            sb.AppendLine($"  {padded} — {desc}");
        }

        return sb.ToString().TrimEnd();
    }

    private string? FindClosestCommand(string name)
    {
        if (_commands.Count == 0) return null;

        var best = _commands.Keys
            .Select(k => (Name: k, Distance: LevenshteinDistance(name.ToLowerInvariant(), k.ToLowerInvariant())))
            .OrderBy(x => x.Distance)
            .First();

        // Only suggest if reasonably close (distance <= half the command name length + 2)
        return best.Distance <= (name.Length / 2) + 2 ? best.Name : null;
    }

    /// <summary>
    /// Compute Levenshtein edit distance between two strings.
    /// Shared by command and setting suggestion helpers.
    /// </summary>
    public static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;

        if (n == 0) return m;
        if (m == 0) return n;

        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var j = 0; j <= m; j++)
            previous[j] = j;

        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[m];
    }

    /// <summary>Get all registered commands (for future help:// integration).</summary>
    public IReadOnlyDictionary<string, CommandRegistration> GetCommands() =>
        _commands.AsReadOnly();
}
