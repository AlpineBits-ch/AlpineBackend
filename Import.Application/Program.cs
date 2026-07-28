using AppEnvironment;
using Import.Application.Discord;
using Import.Application.Gateway;
using Import.Application.Redis;
using Import.Application.Services;
using Import.Contracts;
using Import.Infrastructure;
using Import.Infrastructure.Persistence;
using JasperFx;
using JasperFx.RuntimeCompiler;
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

builder.Services.AddDiscordApiClient();
builder.Services.AddSingleton<DiscordImportStateStore>();
builder.Services.AddScoped<DiscordStructureSyncHandler>();
builder.Services.AddHostedService<DiscordGatewayClient>();
builder.Services.AddHostedService<DiscordStructureReconciliationService>();

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

// Import.Application is Wolverine-HTTP only (no MVC controllers), so authorization must be
// registered explicitly - same reasoning as Bots.Application/Program.cs.
builder.Services.AddAuthorization();

builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.UseWolverine(opts =>
{
    if (args.Contains("facets")) return;
    opts.Discovery.IncludeAssembly(typeof(ImportContractsModule).Assembly);
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts => { });
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
    return;
}

var app = builder.Build();

app.UseInfrastructure();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/import/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);
