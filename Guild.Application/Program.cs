using System.Net.Http.Headers;
using AppEnvironment;
using Facet.Dashboard;
using Guild.Application.Hubs;
using Guild.Application.Services;
using Guild.Persistence;
using Guild.Persistence.Persistence;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Social.Contracts.Services;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;


var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();
builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });



builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var redis = Env.Redis;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));

builder.Services.AddScoped<ProfileService>();
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

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = Env.Redis.ConnectionString;
});
builder.UseWolverine(opts =>
{
    if(args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();
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

builder.Services.AddFacetDashboard();
builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddScoped<GuildHydrateService>();
builder.Services.AddScoped<GuildPermissionService>();
builder.Services.AddScoped<CloudflareService>();
builder.Services.AddScoped<GuildThumbnailService>();
builder.Services.AddHostedService<VoiceHeartbeatCleanupService>();
builder.Services.AddHttpClient("CloudflareProxy", client =>
{
    client.BaseAddress = new Uri($"https://rtc.live.cloudflare.com/v1/apps/{Env.CloudflareConfig.AppId}/");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", Env.CloudflareConfig.ApiToken);
});
if (args.Contains("codegen") || args.Contains("describe"))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { "localhost:6379" },
            AbortOnConnectFail = false,
            AllowAdmin = false,
            Password = null 
        };

        return ConnectionMultiplexer.Connect(options);
    });    
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    codeGenApp.MapFacetDashboard();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);
}

if (args.Contains("facets"))
{
    builder.Services.AddFacetDashboard();
    builder.Services.AddDbContext<MicroserviceContext>();
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { "localhost:6379" }, 
            AbortOnConnectFail = false,
            AllowAdmin = false,
            Password = null 
        };

        return ConnectionMultiplexer.Connect(options);
    });    
    var facetApp = builder.Build();
    facetApp.MapFacetDashboard();
    await facetApp.RunAsync();
    Environment.Exit(0);   
}

var app = builder.Build();

app.MapHub<GuildHub>("api/v1/ws/hubs/guild");
app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/guild/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseInfrastructure();
app.MapControllers();
app.MapFacetDashboard();


await app.RunAsync();
