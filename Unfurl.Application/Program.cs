using System.Net;
using AppEnvironment;
using AppEnvironment.Security;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Unfurl.Application.Caching;
using Unfurl.Application.Fetching;
using Unfurl.Application.Media;
using Unfurl.Application.Parsing;
using Unfurl.Application.Services;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

builder.Services.AddOpenApi();
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddS3Storage();

builder.Services.AddControllers();

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

// The outbound client, and the only one this service has.
//
// Three settings carry the whole SSRF posture and none of them are optional:
//   AllowAutoRedirect = false  - redirects are followed by hand in SafeFetcher so that every hop is
//                                re-validated for scheme and credentials, not just for IP.
//   ConnectCallback            - refuses to dial a non-routable address, checked against the IP
//                                actually being connected to, which is what defeats DNS rebinding.
//   UseCookies = false         - nothing this service fetches should ever carry state between
//                                requests, and a shared cookie jar across users' links is a way to
//                                leak one origin's Set-Cookie into another user's fetch.
builder.Services.AddHttpClient(UnfurlHttpClients.Outbound, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Env.Unfurl.UserAgent);
        client.Timeout = Env.Unfurl.FetchTimeout;
        client.MaxResponseContentBufferSize = Env.Unfurl.MaxImageBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectCallback = OutboundAddressGuard.CreateConnectCallback(Env.Unfurl.AllowPrivateTargets),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });

builder.Services.AddSingleton<MetadataParser>();
builder.Services.AddScoped<SafeFetcher>();
builder.Services.AddScoped<MediaProcessor>();
builder.Services.AddScoped<UnfurlCache>();
builder.Services.AddScoped<UnfurlService>();

builder.Services.AddWolverineHttp();

builder.UseWolverine(opts =>
{
    // No DbContext: this service owns no data. Every answer it gives is either derived from a URL
    // or cached in Redis, which is exactly why it can be scaled and restarted freely, and why the
    // risky half of the feature - arbitrary outbound HTTP - lives here rather than next to the
    // message store. The Postgres connection below is Wolverine's own envelope storage, nothing else.
    opts.ConfigureWolverine(useEfCore: false);

    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
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

var app = builder.Build();

app.MapOpenApi("/internal/openapi/{documentName}.json");

app.MapControllers();
app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();
app.MapHealthChecks("/unfurl/health");

await app.RunJasperFxCommands(args);
