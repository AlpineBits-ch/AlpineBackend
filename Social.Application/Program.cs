using AppEnvironment;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Social.Api.Services;
using Social.Infrastructure;
using Social.Infrastructure.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddInfrastructure();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddScoped<FileService>();
var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});


// Social pushes relationship-lifecycle events (social.*) at the two users involved through
// IHubContext<EchoRealtimeHub>. The hub itself is only mapped on the Echo gateway - the Redis
// backplane is what carries these sends across to the pod holding the user's socket, so the
// backplane registration (not MapHub) is the part Social actually needs. Same wiring as
// Guild.Application/Messaging.Application.
builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

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
    });

builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile
    // bakes in via `dotnet run -- codegen write` before publish. A local/dev/test run from raw
    // build output never runs that step, so fall back to compiling handlers on the fly.
    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        // Dynamic mode compiles handlers with Roslyn at startup - needs an IAssemblyGenerator,
        // which core WolverineFx no longer ships (see JasperFx.RuntimeCompiler package).
        opts.Services.AddRuntimeCompilation();
    }
});
if (args.Contains("codegen") || args.Contains("describe"))
{
    
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);
}
var app = builder.Build();

// Configure the HTTP request pipeline.
// The gateway's docs aggregator fetches this to build the public reference, so it can no longer be
// Development-only. Not publicly reachable: the proxy only forwards /api/v1/{service}/**, so
// nothing routes to /internal from outside.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseHttpsRedirection();
app.MapControllers();

app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/social/health");
app.UseInfrastructure();

await app.RunJasperFxCommands(args);

