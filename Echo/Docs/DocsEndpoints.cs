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
        Normalise(Environment.GetEnvironmentVariable("DOCS_DOMAIN"))
        ?? DeriveFrom(Env.GeneralConfiguration.InstanceUrl);

    /// <summary>
    /// Reduces a configured value to the bare hostname the Host header will actually carry.
    ///
    /// <para>The gating compares against <c>Request.Host.Host</c>, which never contains a scheme,
    /// a port or a path - so <c>DOCS_DOMAIN=https://docs.venta.gg</c> would match nothing and 404
    /// exactly like leaving it unset. That is an easy thing to write and an unpleasant thing to
    /// debug, so it is accepted and reduced rather than rejected.</para>
    /// </summary>
    public static string? Normalise(string? configured)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host.ToLowerInvariant()
                : null;
        }

        // Bare host, possibly with a port or a trailing slash.
        value = value.TrimEnd('/');

        var slash = value.IndexOf('/');
        if (slash > 0) value = value[..slash];

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out _)) value = value[..colon];

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>
    /// Turns the instance URL into the hostname the documentation lives on.
    ///
    /// <para><b>The docs are a sibling of the API, not a child of it.</b> Blindly prefixing
    /// <c>docs.</c> is only right when the instance is on a bare domain. venta.gg runs its gateway
    /// on <c>api.venta.gg</c>, so prefixing produced <c>docs.api.venta.gg</c> - a name with no DNS -
    /// and every request to the real <c>docs.venta.gg</c> fell through to an empty 404. When the
    /// instance is already on a subdomain, that first label is replaced rather than prepended.</para>
    ///
    /// <para>This is still a guess, and a deployment that does not follow the convention should set
    /// <c>DOCS_DOMAIN</c> explicitly. <see cref="MapVentaDocs"/> makes a wrong guess say so instead
    /// of 404ing silently.</para>
    /// </summary>
    public static string DeriveFrom(string? instanceUrl)
    {
        // A misconfigured InstanceUrl must not take the gateway down at startup.
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri)) return "docs.localhost";

        var host = uri.Host;
        if (host.StartsWith("docs.", StringComparison.OrdinalIgnoreCase)) return host;

        var labels = host.Split('.');

        // A bare registrable domain (venta.gg) or a single label (localhost) gets a prefix; an
        // address gets one too, because there is nothing sensible to derive from it.
        if (labels.Length < 3 || System.Net.IPAddress.TryParse(host, out _)) return $"docs.{host}";

        // Already a subdomain: replace its first label. api.venta.gg -> docs.venta.gg.
        return string.Join('.', labels.Skip(1).Prepend("docs"));
    }

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

            if (requested.StartsWith("docs.", StringComparison.OrdinalIgnoreCase)
                && !requested.Equals(host, StringComparison.OrdinalIgnoreCase))
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
