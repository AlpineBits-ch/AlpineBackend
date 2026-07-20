using AppEnvironment;
using Isle.Api.Chat;
using Isle.Api.Chat.CommandController;
using IsleBridge.Sdk;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
// Add services to the container.
builder.Services.AddOpenApi();

builder.Services.AddIsleBridge(cfg =>
{
    cfg.BaseAddress = new Uri("http://10.0.0.21:8080");
    cfg.SlowCommandTimeout = TimeSpan.FromSeconds(10);
});


builder.Services.AddHostedService<ChatWatcher>();
builder.Services.AddHostedService<PresenceService>();
builder.Services.AddHostedService<CommandController>();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();


await app.RunAsync();
