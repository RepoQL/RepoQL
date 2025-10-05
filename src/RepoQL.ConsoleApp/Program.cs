// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Hosting;
using ConsoleAppFramework;
using RepoQL.Core;

var builder = Host.CreateApplicationBuilder(args);


var app = builder.ToConsoleAppBuilder();

app.Add<ServeCommand>();

await app.RunAsync(args); // Run

Console.WriteLine("Hello, World!");