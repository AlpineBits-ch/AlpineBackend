using AppEnvironment;
using Billing.Application.Security;
using Billing.Application.Services;
using Billing.Infrastructure;
using Billing.Infrastructure.Persistence;
using Echo.Auth;
using Echo.Entitlements;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Microsoft.Extensions.Options;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

// Billing hosts no hub and maps no hub route; the gateway does.
builder.Services.AddSignalR(config => config.EnableDetailedErrors = true)
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

// The plan table, from configuration.
builder.Services.AddEntitlements(builder.Configuration);

// The names and prices that go with the configured plans, which the entitlement section has no room
// for and no business holding: it is bound by every service that resolves entitlements, and a price
// is Billing's alone. Read only by the seeder.
builder.Services.Configure<PlanSeedOptions>(
    builder.Configuration.GetSection(PlanSeedOptions.SectionName));

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<EntitlementVersionService>();
builder.Services.AddScoped<GrantService>();

// Scoped, because it reads the plan rows: plans are edited from the console, so a catalogue bound
// once at startup would refuse a plan created ten minutes ago.
builder.Services.AddScoped<PlanCatalogueService>();
builder.Services.AddScoped<PlanService>();

// Billing is the write side, so it registers the grant provider rather than the entitlement source
// built on it.
builder.Services.AddScoped<IGrantProvider>(provider => provider.GetRequiredService<GrantService>());

builder.Services.AddHostedService<GrantExpirySweeper>();
builder.Services.AddBillingPolicies();

builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(_ =>
    {

    });
    opts.ConfigureWolverine();

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

builder.Services.AddVentaJwtBearer();
builder.Services.AddGracefulShutdownHealthCheck();

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);
}

// After the codegen branch above, deliberately: the Dockerfile runs `codegen write` at image build
// time with no environment at all, which reads as selfhost, and an image that cannot be built is a
// worse outcome than a container that will not start.
Env.License.EnsureConfigured();

if (Env.License.IsSelfHost)
{
    throw new InvalidOperationException(
        "Billing does not run in selfhost mode, where every entitlement already resolves to its "
        + "maximum and no billing surface is shown. Remove this service from the stack, or set "
        + "LICENSE_MODE=hosted if this instance is meant to be the hosted product.");
}

var app = builder.Build();

app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/billing/health");

// Configure the HTTP request pipeline.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseHttpsRedirection();
app.UseInfrastructure();
app.MapControllers();

// After the migration, and only on an empty catalogue.
using (var seedScope = app.Services.CreateScope())
{
    await PlanSeeder.SeedAsync(
        seedScope.ServiceProvider.GetRequiredService<MicroserviceContext>(),
        seedScope.ServiceProvider.GetRequiredService<PlanCatalogue>(),
        seedScope.ServiceProvider.GetRequiredService<IOptions<PlanSeedOptions>>().Value,
        seedScope.ServiceProvider.GetRequiredService<TimeProvider>(),
        seedScope.ServiceProvider.GetRequiredService<ILogger<Program>>());
}

await app.RunAsync();
