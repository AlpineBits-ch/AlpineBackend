using AppEnvironment;
using JasperFx;
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

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());
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


builder.Services.AddWolverineHttp();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // The URL of your Auth Server
        options.Authority = "http://identity:8080"; 
        options.RequireHttpsMetadata = false; // Set to true in Prod

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "http://identity:8080",
            ValidateAudience = false,
         
        };
    });

builder.UseWolverine(opts =>
{
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
    Environment.Exit(0);
}
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.MapWolverineEndpoints();
app.MapHealthChecks("/social/health");
app.UseInfrastructure();

await app.RunJasperFxCommands(args);

