using AppEnvironment;
using Echo.Sites;
using Microsoft.Extensions.FileProviders;

namespace Echo.Docs;

/// <summary>
/// Serves the documentation site from the gateway, on its own hostname and nowhere else.
/// </summary>
public static class DocsEndpoints
{
    /// <summary>The docs hostname.</summary>
    public static string Host => SiteHost.Resolve(Label, DomainVariable);

    public const string Label = "docs";

    public const string DomainVariable = "DOCS_DOMAIN";

    /// <summary>
    /// Reduces a configured value to the bare hostname the Host header will actually carry.
    /// </summary>
    public static string? Normalise(string? configured) => SiteHost.Normalise(configured);

    /// <summary>Turns the instance URL into the hostname the documentation lives on.</summary>
    public static string DeriveFrom(string? instanceUrl) => SiteHost.DeriveFrom(Label, instanceUrl);

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

        // A request for some *other* docs.* hostname means the derivation above guessed wrong for
        // this deployment - which is exactly how this shipped broken: docs.venta.gg reached the
        // gateway, the gateway had bound docs.api.venta.gg, and the mismatch surfaced as an empty
        // 404 with nothing anywhere to explain it. Say so instead.
        app.Use(async (context, next) =>
        {
            var requested = context.Request.Host.Host;

            if (Sites.SiteHost.IsMisdirected(requested, host, "docs"))
            {
                app.Logger.LogWarning(
                    "Request for {Requested} but the docs site is bound to {Bound} (derived from "
                    + "INSTANCE_URL={InstanceUrl}). Set DOCS_DOMAIN={Requested} to serve it.",
                    requested, host, Env.GeneralConfiguration.InstanceUrl, requested);

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    $"The documentation site is served on '{host}', not '{requested}'.\n"
                    + $"Set DOCS_DOMAIN={requested} on the gateway to serve it here.\n");
                return;
            }

            await next();
        });

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
