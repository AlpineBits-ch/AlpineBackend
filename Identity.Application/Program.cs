using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Identity.Application.Services.Qr;
using Identity.Application.Services.Steam;
using Identity.Contracts;
using Identity.Domain.Aggregates;
using Identity.Infrastructure;
using Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.RuntimeCompiler;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Messaging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
});
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    // Stated explicitly rather than left to the framework defaults, because every password gate in
    // this service (sign-in, device binding, key-material operations) relies on
    // AccountPasswordVerifier reporting LockedOut. ApplicationUser.Create/CreateBot set
    // LockoutEnabled on the row itself - without that flag these settings do nothing.
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 10;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
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
        options.AllowClientCredentialsFlow();
        options.AllowCustomFlow(SteamOpenIdService.SteamGrantType);
        options.AllowCustomFlow(QrLoginService.QrGrantType);

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
        else
        {
            options.AddDevelopmentSigningCertificate();
            options.AddDevelopmentEncryptionCertificate();
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

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddWolverineHttp()
    
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });
builder.Services.AddInfrastructure();
builder.Services.AddHttpClient<SteamOpenIdService>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<EmailService>();

// Every password-gated route goes through one lockout-aware check, and every per-device rule
// resolves its device from the session rather than from the X-Device-Id header.
builder.Services.AddScoped<Identity.Application.Services.IAccountPasswordVerifier,
    Identity.Application.Services.AccountPasswordVerifier>();
builder.Services.AddScoped<Identity.Application.Services.SessionDeviceResolver>();
builder.Services.AddScoped<Identity.Application.Services.MasterKeyRewrapTicketService>();

// The §I.1 rollout knobs, from configuration rather than from whatever the binary was compiled
// with. Absent or unparsable values keep the safe default - see MlsPolicy.Bind.
Domain.MlsPolicy.Bind(builder.Configuration);
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddHostedService<Identity.Application.Services.AccountDeletionPurgeSweepService>();

builder.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(IdentityContractsModule).Assembly);
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(options =>
    {
        options.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    });

   
    opts.ConfigureWolverine();
    opts.UseFluentValidation();

    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
        opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
        // Dynamic mode compiles handlers with Roslyn at startup - needs an IAssemblyGenerator,
        // which core WolverineFx no longer ships (see JasperFx.RuntimeCompiler package).
        opts.Services.AddRuntimeCompilation();

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
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
forwardedOptions.ForwardLimit = null;

app.UseForwardedHeaders(forwardedOptions);

app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/identity/health");

// Configure the HTTP request pipeline.
// The gateway's docs aggregator fetches this to build the public reference, so it can no longer be
// Development-only. Not publicly reachable: the proxy only forwards /api/v1/{service}/**, so
// nothing routes to /internal from outside.
app.MapOpenApi("/internal/openapi/{documentName}.json");
app.UseAuthentication();
app.UseAuthorization();
//app.UseHttpsRedirection();
app.UseInfrastructure();

app.MapControllers();

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
});



using var scope = app.Services.CreateScope();

var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

var echoApplication = await manager.FindByClientIdAsync("echo");
if (echoApplication == null)
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
            OpenIddictConstants.Permissions.Prefixes.GrantType + SteamOpenIdService.SteamGrantType,
            OpenIddictConstants.Permissions.Prefixes.GrantType + QrLoginService.QrGrantType,
            OpenIddictConstants.Permissions.Scopes.Email,
            OpenIddictConstants.Permissions.Scopes.Profile,
            OpenIddictConstants.Permissions.Scopes.Roles,
        }
    });
}
else
{
    // The block above only runs once, on first-ever startup - on every environment that
    // already has an "echo" client row, a newly added grant type (like the QR login one)
    // would otherwise never reach its permission list. Diff-and-update instead so adding a
    // grant type here always takes effect on the next deploy, not just on a fresh DB.
    var descriptor = new OpenIddictApplicationDescriptor();
    await manager.PopulateAsync(descriptor, echoApplication);
    if (descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.GrantType + QrLoginService.QrGrantType))
    {
        await manager.UpdateAsync(echoApplication, descriptor);
    }
}

await app.RunJasperFxCommands(args);

namespace Identity.Application
{
    public partial class Program { }
}

