
using AppEnvironment;
using Federation.Application;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddControllers();



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



builder.Services.AddFederation();
builder.UseWolverine(opts =>
{

    if (builder.Environment.IsDevelopment())
    {
        return;
    }

    opts.ConfigureWolverine(false);
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
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
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());
var app = builder.Build();

app.UseCors("AlpinePolicy");
app.MapControllers();

app.MapHealthChecks("/federation/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();


await app.RunJasperFxCommands(args);
