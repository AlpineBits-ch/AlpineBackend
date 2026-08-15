using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Identity.Application.Services.Qr;
using Identity.Application.Services.Sso;
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
builder.Services.AddOpenApi();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    // Stated explicitly rather than left to the framework defaults, because every password gate in
    // this service (sign-in, device binding, key-material operations) relies on
    // AccountPasswordVerifier reporting LockedOut.
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
        // The issuer moved off INSTANCE_URL to its own hostname when auth.venta.gg became a real
        // identity provider - see docs/specs/sso.md §2.
        options.SetIssuer(Env.AuthConfiguration.IssuerUrl);
        options.SetTokenEndpointUris("/connect/token");
        options.SetConfigurationEndpointUris("/.well-known/openid-configuration");
        options.SetJsonWebKeySetEndpointUris("/.well-known/jwks");

        // The browser-facing half of the SSO.
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetEndSessionEndpointUris("/connect/logout");
        options.SetUserInfoEndpointUris("/connect/userinfo");
        options.SetRevocationEndpointUris("/connect/revoke");

        options.AllowPasswordFlow();
        options.AllowRefreshTokenFlow();
        options.AllowClientCredentialsFlow();
        options.AllowCustomFlow(SteamOpenIdService.SteamGrantType);
        options.AllowCustomFlow(QrLoginService.QrGrantType);

        // PKCE is required of every client, confidential ones included.
        options.AllowAuthorizationCodeFlow()
               .RequireProofKeyForCodeExchange();

        // The scopes a client may ask for.
        options.RegisterScopes(
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Roles);

        if (builder.Environment.IsProduction())
        {

            var certificateBase64 = Env.AuthConfiguration.IdentitySigningCert;

            if (string.IsNullOrWhiteSpace(certificateBase64))
            {
                // Refusing to start is the point.
                throw new InvalidOperationException(
                    "IDENTITY_SIGNING_CERT is not set. Identity refuses to start in Production "
                    + "without a persistent signing certificate: the development fallback is "
                    + "regenerated on every restart, which silently invalidates every token the "
                    + "instance has issued. Re-run the installer, or generate a PKCS#12 bundle and "
                    + "set IDENTITY_SIGNING_CERT to its base64 encoding and IDENTITY_KEY_PASSWORD "
                    + "to its password.");
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
            .EnableAuthorizationEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
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
// Two schemes, and which one is the default matters.
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
    .AddCookie(SsoCookie.Scheme, options =>
    {
        options.Cookie.Name = SsoCookie.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";

        // Required by the __Host- prefix, and never relaxed for local development: browsers already
        // treat http://localhost as a secure context, so nothing needs the exception.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;

        options.ExpireTimeSpan = SsoCookie.SlidingLifetime;
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = SsoCookie.EnforceAbsoluteLifetimeAsync;

        // This scheme never redirects.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// In-flight authorization requests, parked in Redis while somebody signs in.
builder.Services.AddScoped<AuthorizationRequestStash>();
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

// Keeps the Graph sendMail call off the request path for the anonymous "send me a code" routes, so
// the account-exists and no-such-account branches take the same time as well as returning the same
// 202. Singleton: it holds no per-request state and opens its own scope per send.
builder.Services.AddSingleton<Identity.Application.Services.AccountEmailDispatcher>();

// Billing mail (credit issued, a hand-made grant changed, a plan went up).
builder.Services.AddSingleton<Identity.Application.Templates.EmailTemplateRenderer>();
builder.Services.AddScoped<Identity.Application.Services.IBillingMailSender,
    Identity.Application.Services.GraphBillingMailSender>();
builder.Services.AddScoped<Identity.Application.Services.BillingMailService>();

// Every password-gated route goes through one lockout-aware check, and every per-device rule
// resolves its device from the session rather than from the X-Device-Id header.
builder.Services.AddScoped<Identity.Application.Services.IAccountPasswordVerifier,
    Identity.Application.Services.AccountPasswordVerifier>();
builder.Services.AddScoped<Identity.Application.Services.SessionDeviceResolver>();
builder.Services.AddScoped<Identity.Application.Services.MasterKeyRewrapTicketService>();

// The §I.1 rollout knobs, from configuration rather than from whatever the binary was compiled
// with. Absent or unparsable values keep the safe default - see MlsPolicy.Bind.
Domain.MlsPolicy.Bind(builder.Configuration);
// Versioned consent (T1-10) and the legal documents it points at (T1-12).
builder.Services.AddScoped<Identity.Application.Services.ConsentService>();
builder.Services.AddSingleton<Identity.Application.Services.LegalDocumentCatalog>();

// T0-4's consent hook, finally attached to a real lookup.
builder.Services.AddSingleton<Identity.Application.Services.DataCollectionConsentSnapshot>();
builder.Services.AddHostedService<Identity.Application.Services.DataCollectionConsentRefreshService>();

builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddHostedService<Identity.Application.Services.AccountDeletionPurgeSweepService>();

// T1-8. Nothing in this system had a TTL before this loop existed.
builder.Services.AddHostedService<Identity.Application.Services.RetentionSweepService>();

// T1-7.
builder.Services.AddS3Storage();
builder.Services.AddScoped<Identity.Application.Services.DataExport.IDataExportArtifactStore,
    Identity.Application.Services.DataExport.S3DataExportArtifactStore>();
builder.Services.AddHostedService<Identity.Application.Services.DataExport.DataExportExpirySweepService>();

// Publishes whatever legal documents this build ships, and re-hashes them so an edit to a published
// document is visible in the log instead of silent.
builder.Services.AddHostedService<Identity.Application.Services.LegalDocumentSeeder>();

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

// Creates the first-party `echo` client, and backfills any permission an existing row is missing.
await Identity.Application.Services.EchoClientBootstrap.EnsureAsync(manager);

// The SSO client allowlist (docs/specs/sso.md §7).
{
    var registryLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger(typeof(AuthClientRegistry));

    await AuthClientRegistry.ReconcileAsync(
        manager, AuthClientRegistry.Parse(Env.AuthConfiguration.Clients, registryLogger), registryLogger);
}

await app.RunJasperFxCommands(args);

namespace Identity.Application
{
    public partial class Program { }
}

