using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using Projects;
using RepoQL.Orchestrator;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<RepoQlHostHealthCheck>("repoql-host");

var embedding = builder.AddProject<RepoQL_Embedding_Service>("embedding")
    .WithHttpEndpoint(name: "grpc", targetPort: 8080)
    .WithCommand(
        name: "smoke_test",
        displayName: "Smoke test (GetModelInfo)",
        executeCommand: async context =>
        {
            try
            {
                var ct = context.CancellationToken;
                var model = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();
                var resource = model.Resources.OfType<ProjectResource>().First(r => r.Name == "embedding");
                var endpoint = resource.GetEndpoint("grpc");
                var url = endpoint.Url;

                using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(url, new Grpc.Net.Client.GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true }
                });
                var client = new RepoQL.Embedding.EmbeddingService.EmbeddingServiceClient(channel);
                var info = await client.GetModelInfoAsync(new RepoQL.Embedding.GetModelInfoRequest(), cancellationToken: ct);

                context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("EmbeddingSmokeTest")
                    .LogInformation("GetModelInfo succeeded: model={Model}, dimension={Dimension}", info.Model, info.Dimension);
                return CommandResults.Success();
            }
            catch (Exception ex)
            {
                return CommandResults.Failure(ex);
            }
        },
        commandOptions: new CommandOptions
        {
            Description = "Calls GetModelInfo to verify the embedding service is responding.",
            IconName = "Checkmark",
            IsHighlighted = true
        });

var host = builder.AddProject<RepoQL_ConsoleApp>("host", options => options.LaunchProfileName = "host")
    .WithHealthCheck("repoql-host")
    .WithReference(embedding)
    .WithCommand(
        name: "rebuild_and_restart",
        displayName: "Rebuild and restart",
        executeCommand: async context =>
        {
            try
            {
                var ct = context.CancellationToken;
                var resourceCommands = context.ServiceProvider.GetRequiredService<ResourceCommandService>();

                // Stop the host
                var stopResult = await resourceCommands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StopCommand,
                        ct)
                    .ConfigureAwait(false);

                if (stopResult.Canceled)
                    return CommandResults.Canceled();
                if (!stopResult.Success)
                    return stopResult.ErrorMessage is { Length: > 0 }
                        ? CommandResults.Failure($"Failed to stop host: {stopResult.ErrorMessage}")
                        : CommandResults.Failure("Failed to stop host.");

                // Build the project
                var repoRoot = ResolveRepoRoot();
                var projectPath = Path.Combine(repoRoot, "src", "RepoQL.ConsoleApp", "RepoQL.ConsoleApp.csproj");

                var buildProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"build \"{projectPath}\" --no-restore",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                buildProcess.Start();
                await buildProcess.WaitForExitAsync(ct).ConfigureAwait(false);

                if (buildProcess.ExitCode != 0)
                {
                    var stderr = await buildProcess.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    return CommandResults.Failure($"Build failed (exit code {buildProcess.ExitCode}): {stderr}");
                }

                // Start the host
                var startResult = await resourceCommands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StartCommand,
                        ct)
                    .ConfigureAwait(false);

                if (startResult.Canceled)
                    return CommandResults.Canceled();
                if (!startResult.Success)
                    return startResult.ErrorMessage is { Length: > 0 }
                        ? CommandResults.Failure($"Failed to start host: {startResult.ErrorMessage}")
                        : CommandResults.Failure("Failed to start host.");

                return CommandResults.Success();
            }
            catch (OperationCanceledException)
            {
                return CommandResults.Canceled();
            }
            catch (Exception ex)
            {
                return CommandResults.Failure(ex);
            }
        },
        commandOptions: new CommandOptions
        {
            Description = "Stops the RepoQL host, rebuilds it, then restarts it. Use after code changes.",
            IconName = "ArrowSync",
            IsHighlighted = true
        })
    .WithCommand(
        name: "reset_repoql_directory",
        displayName: "Reset .repoql and restart",
        executeCommand: async context =>
        {
            try
            {
                var ct = context.CancellationToken;
                var resourceCommands = context.ServiceProvider.GetRequiredService<ResourceCommandService>();

                var repoRoot = ResolveRepoRoot();
                var repoqlDir = Path.Combine(repoRoot, ".repoql");

                var stopResult = await resourceCommands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StopCommand,
                        ct)
                    .ConfigureAwait(false);

                if (stopResult.Canceled)
                    return CommandResults.Canceled();
                if (!stopResult.Success)
                    return stopResult.ErrorMessage is { Length: > 0 }
                        ? CommandResults.Failure($"Failed to stop host: {stopResult.ErrorMessage}")
                        : CommandResults.Failure("Failed to stop host.");

                await DeleteDirectoryWithRetriesAsync(repoqlDir, timeout: TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

                var startResult = await resourceCommands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StartCommand,
                        ct)
                    .ConfigureAwait(false);

                if (startResult.Canceled)
                    return CommandResults.Canceled();
                if (!startResult.Success)
                    return startResult.ErrorMessage is { Length: > 0 }
                        ? CommandResults.Failure($"Failed to start host: {startResult.ErrorMessage}")
                        : CommandResults.Failure("Failed to start host.");

                return CommandResults.Success();
            }
            catch (OperationCanceledException)
            {
                return CommandResults.Canceled();
            }
            catch (Exception ex)
            {
                return CommandResults.Failure(ex);
            }
        },
        commandOptions: new CommandOptions
        {
            Description = "Stops the RepoQL host, deletes the repository-local .repoql directory, then restarts the host.",
            ConfirmationMessage = "This will delete the entire .repoql directory for the repository (index DB, imports, UI state). Continue?",
            IconName = "Delete",
            IsHighlighted = true
        });


builder.Build().Run();

static string ResolveRepoRoot()
{
    var explicitWorkingDirectory = Environment.GetEnvironmentVariable("REPOQL_CWD");
    if (!string.IsNullOrWhiteSpace(explicitWorkingDirectory) &&
        !explicitWorkingDirectory.Contains('{') &&
        Directory.Exists(explicitWorkingDirectory))
    {
        return RepoLocator.FindRepoRoot(explicitWorkingDirectory);
    }

    return RepoLocator.FindRepoRoot();
}

static async Task DeleteDirectoryWithRetriesAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
{
    if (!Directory.Exists(path))
        return;

    var sw = Stopwatch.StartNew();
    while (true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            MakeDirectoryDeletable(path);
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (sw.Elapsed >= timeout)
                throw;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }
}

static void MakeDirectoryDeletable(string directory)
{
    if (!Directory.Exists(directory))
        return;

    try
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch { /* best-effort */ }
        }

        foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(dir);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(dir, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch { /* best-effort */ }
        }
    }
    catch
    {
        // best-effort; deletion will retry and surface failures if persistent
    }
}
