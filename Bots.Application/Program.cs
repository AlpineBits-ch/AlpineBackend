using AppEnvironment;
using Bots.Application.Gateway;
using Bots.Application.Middleware;
using Bots.Contracts;
using Bots.Infrastructure;
using Bots.Infrastructure.Persistence;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();
builder.Services.AddGracefulShutdownHealthCheck();

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));

builder.Services.AddHttpClient();

builder.Services.AddSingleton<BotTokenTranslator>();
builder.Services.AddSingleton<GatewayConnectionRegistry>();
builder.Services.AddScoped<GatewayHandshakeService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = Env.GeneralConfiguration.InstanceUrl;
        options.RequireHttpsMetadata = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Env.GeneralConfiguration.InstanceUrl,
            ValidateAudience = false,
        };
    });

// Other services get this transitively via AddControllers(); Bots.Application is
// Wolverine-HTTP only (no MVC controllers), so it must be registered explicitly for
// UseAuthorization() / the [Authorize] attribute on the endpoint classes to work.
builder.Services.AddAuthorization();

builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.UseWolverine(opts =>
{
    if (args.Contains("facets")) return;
    opts.Discovery.IncludeAssembly(typeof(BotsContractsModule).Assembly);
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {
    });
    opts.ConfigureWolverine();
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    await codeGenApp.RunJasperFxCommands(args);
    return;
}

var app = builder.Build();

app.UseInfrastructure();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/bots/health");

// Serves wwwroot/index.html - the small bot developer portal for generating credentials.
// Reachable through the gateway at /bots-portal (see Echo/Proxy/ProxyConfig.cs).
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWebSockets();

// The Gateway WebSocket upgrade sits before everything else, including
// DiscordBotTokenTranslationMiddleware/UseAuthentication() - Gateway auth isn't HTTP-header-based
// at all, it happens inside the OP 2 Identify payload (see GatewayConnection). A plain (non-
// upgrade) GET to the same path falls through to DiscordGatewayEndpoint's REST discovery response.
app.UseWhen(
    ctx => ctx.WebSockets.IsWebSocketRequest && ctx.Request.Path.StartsWithSegments("/api/discord/v10/gateway"),
    branch => branch.UseMiddleware<GatewayWebSocketMiddleware>());

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/discord/v10"),
    branch => branch.UseMiddleware<DiscordBotTokenTranslationMiddleware>());

app.UseAuthentication();
app.UseAuthorization();

app.MapWolverineEndpoints();

app.Services.GetRequiredService<GatewayConnectionRegistry>().Start();

// Converts a pod drain into a clean, library-expected reconnect instead of a hard connection
// drop - pairs with AddGracefulShutdownHealthCheck() already pulling this pod out of Service
// rotation for *new* connections while these existing ones get told to reconnect elsewhere.
app.Lifetime.ApplicationStopping.Register(() =>
{
    var registry = app.Services.GetRequiredService<GatewayConnectionRegistry>();
    var sendTasks = registry.LocalConnections.Select(c => c.SendReconnectAsync()).ToArray();
    Task.WaitAll(sendTasks, TimeSpan.FromSeconds(5));
});

await app.RunJasperFxCommands(args);
