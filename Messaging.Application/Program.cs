using System.Net.Http.Headers;
using Echo.Realtime.Sfu;
using AppEnvironment;
using Domain;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Messaging.Application.Services;
using Messaging.Infrastructure;
using Messaging.Infrastructure.Persistence;
using Messaging.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();


builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddScoped<FileService>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});
builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
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
builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });
builder.Services.AddHttpClient("CloudflareRtc", client =>
{
    client.BaseAddress = new Uri("https://rtc.live.cloudflare.com/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Env.CloudflareConfig.ApiToken);
});
builder.Services.AddCloudflareCalls(Env.CloudflareConfig.AppId, Env.CloudflareConfig.ApiToken);
builder.Services.AddScoped<ConversationPermissionService>();
builder.Services.AddScoped<IceServerService>();
if (args.Contains("codegen") || args.Contains("describe"))
{
    var debugScylla = ScyllaContext.CreateDebug();
    builder.Services.AddSingleton(debugScylla);
    var jasperApp = builder.Build();
    jasperApp.MapWolverineEndpoints();
    await jasperApp.RunJasperFxCommands(args);
    return;
}

var scylla = await ScyllaContext.CreateAsync();
builder.Services.AddSingleton(scylla);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = Env.GeneralConfiguration.InstanceUrl; 
        options.RequireHttpsMetadata = false; // Set to true in Prod

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
            }
        };
    });

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
var app = builder.Build();
app.UseInfrastructure();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


app.UseHttpsRedirection();

app.MapControllers();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/messaging/health");

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);

