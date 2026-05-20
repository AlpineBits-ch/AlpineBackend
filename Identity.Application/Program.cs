using System.Security.Cryptography.X509Certificates;
using System.Text;
using AppEnvironment;
using Identity.Application;
using Identity.Contracts;
using Identity.Domain.Aggregates;
using Identity.Infrastructure;
using Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.CodeGeneration;
using Messaging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;


var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    
}).AddSignInManager()
.AddEntityFrameworkStores<MicroserviceContext>()
.AddDefaultTokenProviders();
var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore().UseDbContext<MicroserviceContext>();
        options.UseQuartz(); 
    })
    .AddServer(options =>
    {        
        options.SetIssuer(Env.GeneralConfiguration.InstanceUrl);
        options.SetTokenEndpointUris("/connect/token");
        options.SetConfigurationEndpointUris("/.well-known/openid-configuration");
        options.SetJsonWebKeySetEndpointUris("/.well-known/jwks");

        options.AllowPasswordFlow();
        options.AllowRefreshTokenFlow();

        if (builder.Environment.IsProduction())
        {

            var certificateBase64 = Env.AuthConfiguration.IdentitySigningCert;

            if (string.IsNullOrWhiteSpace(certificateBase64))
            {
                options.AddDevelopmentSigningCertificate();
                options.AddDevelopmentEncryptionCertificate();
            }
            else
            {
                var certificate = X509CertificateLoader.LoadPkcs12(
                    Convert.FromBase64String(certificateBase64),
                    password: Env.AuthConfiguration.IdentitySecretPassword,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
  

                options.AddSigningCertificate(certificate);
                options.AddEncryptionCertificate(certificate);
            }
        }
      
    
        
        options.UseAspNetCore()
            .EnableTokenEndpointPassthrough()
            .DisableTransportSecurityRequirement();
        options.DisableAccessTokenEncryption();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

builder.Services.AddControllers();
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddWolverineHttp()
    
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });
builder.Services.AddInfrastructure();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<EmailService>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());
builder.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(IdentityContractsModule).Assembly);
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();
    opts.UseFluentValidation();

    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
    }
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    
    try
    {
        var codeGenApp = builder.Build();
        codeGenApp.MapWolverineEndpoints(opts =>
        {
            opts.UseFluentValidationProblemDetailMiddleware();
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

var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

app.Use((context, next) =>
{
    if (context.Request.Host.Host.Equals("api.venta.gg", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Scheme = "https";
    }
    return next();
});
app.MapHealthChecks("/identity/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseInfrastructure();

app.MapControllers();

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
});



using var scope = app.Services.CreateScope();

var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

if (await manager.FindByClientIdAsync("echo") == null)
{
    await manager.CreateAsync(new OpenIddictApplicationDescriptor
    {
        ClientId = "echo",
        DisplayName = "Echo Application",
        Permissions =
        {
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.GrantTypes.Password,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
            OpenIddictConstants.Permissions.Scopes.Email,
            OpenIddictConstants.Permissions.Scopes.Profile,
            OpenIddictConstants.Permissions.Scopes.Roles,
        }
    });
}

await app.RunJasperFxCommands(args);

