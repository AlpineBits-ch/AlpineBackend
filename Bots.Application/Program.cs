using AppEnvironment;
using Bots.Application.Middleware;
using Bots.Contracts;
using Bots.Infrastructure;
using Bots.Infrastructure.Persistence;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
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

builder.Services.AddHttpClient();

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

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/discord/v10"),
    branch => branch.UseMiddleware<DiscordBotTokenTranslationMiddleware>());

app.UseAuthentication();
app.UseAuthorization();

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);
