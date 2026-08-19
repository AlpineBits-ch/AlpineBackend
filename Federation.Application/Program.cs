using Echo.Auth;

using AppEnvironment;
using Federation.Application;
using Federation.Application.Security;
using Microsoft.AspNetCore.Authorization;
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
builder.Services.AddScoped<Federation.Application.Services.UserService>();
builder.Services.AddHostedService<Federation.Application.Services.FederationOutboundRetryService>();
builder.Services.AddHostedService<Federation.Application.Services.FederationDagGcService>();

// UserService (used by GetUserProfileAsync/GetFederatedUserId, wired up for real in Phase 1/2 of
// the federation work) caches profile lookups via IDistributedCache - previously unregistered
// here since UserService was never actually constructed through DI before.
var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});
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
builder.Services.AddVentaJwtBearer();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(FederationPolicies.InstanceAdmin, policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new InstanceAdminRequirement()));
});
builder.Services.AddScoped<IAuthorizationHandler, InstanceAdminHandler>();

builder.Services.AddHttpClient();

// The handshake target is caller-supplied, so this client refuses redirects and IP-checks every
// connection (see FederationTargetGuard).
var allowPrivateFederationTargets = !builder.Environment.IsProduction();
builder.Services.AddHttpClient(FederationHttpClients.Handshake)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = FederationTargetGuard.CreateConnectCallback(allowPrivateFederationTargets),
    });



builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });

    // Previously returned here in Development, skipping ConfigureWolverine (and therefore RabbitMQ)
    // entirely - meaning Federation could never send/receive the cross-service bus messages the
    // rest of federation wiring depends on outside Production.
    opts.ConfigureWolverine();
    // Reflection-based serialization for bus messages, exactly like the other seven services.
    //
    // This used to install EventJsonContext and FederationMessageContext as the *entire*
    // TypeInfoResolverChain. Because that chain had no reflection fallback, any message type not
    // explicitly annotated in one of those two contexts could not be deserialized at all - and the
    // failure was invisible in every way that matters: it is not a compile error, not a startup
    // error, and produces no routing warning. The envelope is taken off the queue, dies in
    // HandlerPipeline.TryDeserializeEnvelope before the handler is ever invoked, and is moved to the
    // error queue.
    //
    // Both cross-service lifecycle commands were missing from those contexts, so in production
    // Federation silently answered neither:
    //   - ExportUserDataCommand  -> every data export resolved Partial, naming federation
    //   - PurgeUserDataCommand   -> every account deletion hung, because AccountDeletionSaga
    //                               deliberately never self-completes on its deadline
    // Both are GDPR paths, and both failed with no error surfaced anywhere except the dead-letter
    // queue, which nothing consumes or alerts on.
    //
    // Adding the two missing types would have fixed today's symptom and left the trap armed for the
    // next contract added to the fan-out. Nothing here needs source generation - Federation is not
    // trimmed or AOT-published - so the whole hazard goes away with it.
    //
    // The HTTP configuration above deliberately keeps EventJsonContext: those options come from
    // ASP.NET with a reflection resolver already in the chain, so inserting a context there only
    // prioritises it and still falls back. That is the safe shape; this was not.
    opts.UseSystemTextJsonForSerialization(FederationBusSerialization.Configure);

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

    return await codeGenApp.RunJasperFxCommands(args);
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AlpinePolicy", policy =>
    {
        // Shared with the gateway via AppEnvironment.ClientOrigins rather than repeated.
        policy.WithOrigins([.. ClientOrigins.Allowed])
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

// Explicit rather than relying on WebApplication's auto-insertion: the federation admin surface
// now depends on an authorization policy, and that dependency should not rest on inferred wiring.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/federation/health");
app.MapWolverineEndpoints();
// The gateway's docs aggregator fetches this to build the public reference, so it can no longer be
// Development-only.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseHttpsRedirection();


return await app.RunJasperFxCommands(args);
