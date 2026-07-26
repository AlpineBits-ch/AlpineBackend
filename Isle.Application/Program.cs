using AppEnvironment;
using Echo.Realtime.Sfu;
using Isle.Api.Extensions;
using Isle.Infrastructure;
using Isle.Infrastructure.Persistence;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
var redis = Env.Redis;

builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
builder.Services.AddOpenApi();
builder.Services.AddWolverineHttp();

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
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];

                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/api/v1/ws/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.UseWolverine(opts =>
{
    if (args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(_ => { });
    opts.ConfigureWolverine();
});

builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddInfrastructure();

// Cloudflare Calls SFU signalling relay for proximity voice.
builder.Services.AddCloudflareCalls(Env.CloudflareConfig.AppId, Env.CloudflareConfig.ApiToken);

builder.Services.AddIsleApplication();

if (args.Contains("codegen") || args.Contains("describe"))
{
    try
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { "localhost:6379" },
                AbortOnConnectFail = false,
                AllowAdmin = false,
                Password = null,
            }));

        var codeGenApp = builder.Build();
        codeGenApp.MapWolverineEndpoints(opts =>
        {
            opts.UseDataAnnotationsValidationProblemDetailMiddleware();
        });
        await codeGenApp.RunJasperFxCommands(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"CODEGEN BUILD FAILED: {ex}");
        throw;
    }

    return;
}

var app = builder.Build();

app.UseInfrastructure();

await app.SeedGameModeDefinitionsAsync();
await app.SeedQuestsAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseGracefulShutdownHealthCheck();
app.MapWolverineEndpoints();
app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();

await app.RunAsync();
