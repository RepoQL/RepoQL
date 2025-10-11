using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<RepoQL_ConsoleApp>("host", options => options.LaunchProfileName = "host");

//builder.AddProject<RepoQL_ConsoleApp>("cli", options => options.LaunchProfileName = "xray");

builder.Build().Run();
