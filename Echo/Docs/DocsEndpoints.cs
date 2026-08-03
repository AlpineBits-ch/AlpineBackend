using AppEnvironment;
using Microsoft.Extensions.FileProviders;

namespace Echo.Docs;

/// <summary>
/// Serves the documentation site from the gateway, on its own hostname and nowhere else.
///
/// <para><b>Host-gated, deliberately.</b> docs.venta.gg points at this same gateway, so the docs are
/// distinguishable from API traffic by Host header alone - and nothing here is reachable on the API
/// host. That keeps one public surface per hostname: no <c>/docs</c> path shadowing a future API
/// route, and no chance of the vendored renderer bundles being served next to the API.</para>
///
/// <para>The site lives at the <em>root</em> of the docs host: <c>/</c>, <c>/openapi.json</c>,
/// <c>/asyncapi.json</c>, <c>/vendor/*</c>. Static files are branched with <c>UseWhen</c> rather than
/// endpoint metadata because <c>UseStaticFiles</c> is middleware - <c>RequireHost</c> only applies to
/// routed endpoints and would not gate it.</para>
///
/// <para>All of it is registered before <c>MapReverseProxy</c>, otherwise YARP's catch-all routes
/// swallow the requests.</para>
/// </summary>
public static class DocsEndpoints
{
    /// <summary>
    /// The docs hostname. Derived from the instance URL so a self-hosted deployment gets a working
    /// docs site on its own domain, and overridable with <c>DOCS_DOMAIN</c>.
    /// </summary>
    public static string Host =>
        Environment.GetEnvironmentVariable("DOCS_DOMAIN") ?? DefaultHost();

    private static string DefaultHost() =>
        // A misconfigured InstanceUrl must not take the gateway down at startup.
        Uri.TryCreate(Env.GeneralConfiguration.InstanceUrl, UriKind.Absolute, out var uri)
            ? $"docs.{uri.Host}"
            : "docs.localhost";

    public static IServiceCollection AddVentaDocs(this IServiceCollection services)
    {
        services.AddHttpClient("docs");
        services.AddSingleton<OpenApiAggregator>();
        return services;
    }

    public static WebApplication MapVentaDocs(this WebApplication app)
    {
        var host = Host;
        var root = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "docs");

        if (!Directory.Exists(root))
        {
            app.Logger.LogWarning("Docs assets not found at {Root}; the docs host will 404", root);
            return app;
        }

        app.Logger.LogInformation("Docs site bound to host {DocsHost}", host);

        var files = new PhysicalFileProvider(root);

        // Only on the docs host - see the class remarks for why this is UseWhen and not RequireHost.
        app.UseWhen(
            context => context.Request.Host.Host.Equals(host, StringComparison.OrdinalIgnoreCase),
            branch =>
            {
                branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    // The renderer bundles are large and content-addressed by release, so a long
                    // cache is safe; the two generated documents set their own shorter lifetimes.
                    OnPrepareResponse = ctx =>
                    {
                        if (ctx.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                            ctx.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400";
                        }
                    },
                });
            });

        var docs = app.MapGroup("").RequireHost(host);

        docs.MapGet("/openapi.json", async (OpenApiAggregator aggregator, HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=60";
            await context.Response.WriteAsync(await aggregator.GetDocumentAsync(context.RequestAborted));
        });

        docs.MapGet("/asyncapi.json", async (HttpContext context) =>
        {
            var path = Path.Combine(root, "asyncapi.json");
            if (!File.Exists(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync(
                    "asyncapi.json was not generated into this image - run Docs.Generator during the build.");
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=300";
            await context.Response.SendFileAsync(path);
        });

        return app;
    }
}
