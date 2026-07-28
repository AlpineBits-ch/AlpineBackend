
using AppEnvironment;
using Federation.Application;
using Federation.Application.Dtos.Events;
using Federation.Application.Messages;
using Federation.Application.Providers;
using Federation.Application.Services;
using Federation.Infrastructure;
using Federation.Infrastructure.Persistence;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddSingleton<IFederatedDomainResolver, VentaDomainResolver>();
builder.Services.AddScoped<Federation.Application.Services.FederationDagService>();
builder.Services.AddScoped<IFederationProvider, VentaFederationProvider>();
builder.Services.AddScoped<IFederationAcceptanceEvaluator, PolicyBasedEvaluator>();
builder.Services.AddScoped<FederationHandshakeService>();
builder.Services.AddWolverineHttp().ConfigureSystemTextJsonForWolverineOrMinimalApi(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, EventJsonContext.Default);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, EventJsonContext.Default);
});
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
builder.Services.AddHttpClient();



builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });

    // Previously returned here in Development, skipping ConfigureWolverine (and therefore RabbitMQ)
    // entirely - meaning Federation could never send/receive the cross-service bus messages the
    // rest of federation wiring depends on outside Production.
    opts.ConfigureWolverine();
    opts.UseSystemTextJsonForSerialization(o =>
    {
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        o.TypeInfoResolverChain.Insert(0, EventJsonContext.Default);
        o.TypeInfoResolverChain.Insert(1, FederationMessageContext.Default);

    });

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile bakes
    // in via `dotnet run -- codegen write` before publish.
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

builder.Services.AddCors(options =>
{
    options.AddPolicy("AlpinePolicy", policy =>
    {
        policy.WithOrigins("http://localhost:1420", "https://chat.alpinebits.ch", "http://tauri.localhost", "tauri://localhost")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddGracefulShutdownHealthCheck();


var app = builder.Build();
app.UseGracefulShutdownHealthCheck();

app.UseInfrastructure();

app.UseCors("AlpinePolicy");
app.MapControllers();

app.MapHealthChecks("/federation/health");
app.MapWolverineEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();


await app.RunJasperFxCommands(args);
