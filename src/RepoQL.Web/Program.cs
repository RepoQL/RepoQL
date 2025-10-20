using MudBlazor.Services;
using RepoQL.Web.Components;
using RepoQL.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<RepoQlConnectionManager>();
builder.Services.AddSingleton<HostStatusStore>();
builder.Services.AddHostedService<HostStatusService>();
builder.Services.AddScoped<SqlExecutionService>();
builder.Services.AddScoped<DocumentExplorerService>();
builder.Services.AddMudServices();
builder.Services.AddScoped<StatsService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Services.GetRequiredService<RepoQlConnectionManager>().StartWarmup();

app.Run();
