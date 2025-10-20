using Microsoft.Extensions.DependencyInjection;
using Projects;
using RepoQL.Orchestrator;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddCheck<RepoQlHostHealthCheck>("repoql-host");

var host = builder.AddProject<RepoQL_ConsoleApp>("host", options => options.LaunchProfileName = "host")
    .WithHealthCheck("repoql-host");
builder.AddProject<RepoQL_Web>("web")
    .WaitFor(host);

builder.Build().Run();
