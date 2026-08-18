using System.Threading.RateLimiting;
using AppEnvironment;
using Echo.RateLimiter;
using Echo.Sites;
using Microsoft.AspNetCore.RateLimiting;

namespace Echo.Wiki;

/// <summary>Marks the endpoints that make up the wiki site, so nothing else may answer on its host.</summary>
public sealed class WikiSiteEndpoint;

/// <summary>A budget of its own for the anonymous wiki host.</summary>
public static class WikiRateLimiting
{
    public const string PolicyName = "WikiReadPolicy";

    public const int RequestsPerMinute = 60;

    public static IServiceCollection AddWikiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter => limiter.AddPolicy(PolicyName, context =>
        {
            var options = context.RequestServices.GetService<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
            var address = ClientIpResolver.Resolve(context, options)?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter($"wiki:{address}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }));

        return services;
    }
}

/// <summary>
/// Serves published guild wikis from the gateway, each on its own hostname and nowhere else.
/// </summary>
public static class WikiHosting
{
    public const string Label = "wiki";

    public const string DomainVariable = "WIKI_DOMAIN";

    /// <summary>Where a reverse proxy asks whether a hostname deserves a certificate.</summary>
    public const string CertificateCheckPath = "/api/v1/wiki/certificate-check";

    /// <summary>The wiki site's apex. Each published wiki answers on a label beneath it.</summary>
    public static string Host => SiteHost.Resolve(Label, DomainVariable);

    /// <summary>
    /// How long a rendered page may be reused. Wiki content changes on human edit timescales, and an
    /// uncached anonymous route into Postgres is a free amplification target. The cost is that an
    /// unpublish takes up to this long to stop being served.
    /// </summary>
    public const int PageCacheSeconds = 60;

    /// <summary>Every path the wiki host answers on. Everything else there is a 404.</summary>
    public static IServiceCollection AddVentaWiki(this IServiceCollection services)
    {
        services.AddHttpClient<WikiServiceClient>(client =>
        {
            client.BaseAddress = new Uri(WikiServiceClient.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddWikiRateLimiting();

        return services;
    }

    public static WebApplication MapVentaWiki(this WebApplication app)
    {
        var host = Host;
        var webRoot = app.Environment.WebRootPath ?? "wwwroot";

        app.Logger.LogInformation(
            "Public wiki bound to host {WikiHost}; each published wiki answers on {Pattern}",
            host, $"<slug>.{host}");

        app.UseSiteHostDiagnostics(Label, host, DomainVariable, "public wiki");

        MapCertificateCheck(app, host);

        app.UseWhen(
            context => WikiHost.Match(context.Request.Host.Host, host).IsWikiHost,
            branch =>
            {
                branch.UseWikiSiteSecurity();

                // The gateway's routes match on path alone, so without this every service route
                // answers here too. That has been harmless for admin, support and status because
                // none of them render third-party content; this is the first host that does, and an
                // XSS here having a same-origin path to the API is the thing not to leave lying
                // around. Routing has already run, so the selected endpoint is the whole check.
                branch.Use(async (context, next) =>
                {
                    if (context.GetEndpoint()?.Metadata.GetMetadata<WikiSiteEndpoint>() is null)
                    {
                        await WriteAsync(context, WikiPageRenderer.NotFound(RenderContext(host)),
                            StatusCodes.Status404NotFound);
                        return;
                    }

                    await next();
                });
            });

        // The apex and one label beneath it. RequireHost's wildcard matches any depth, so how deep
        // the label is stays WikiHost's decision rather than routing's.
        var wiki = app.MapGroup("")
            .RequireHost(host, $"*.{host}")
            .RequireRateLimiting(WikiRateLimiting.PolicyName)
            .WithMetadata(new WikiSiteEndpoint());

        // Served as endpoints rather than static files, and this is load-bearing rather than a
        // preference: StaticFileMiddleware stands down once routing has picked an endpoint, so any
        // file under a path that {slug} or {slug}/{pageSlug} also matches would silently stop being
        // served. The site is two assets, so there is nothing to gain by risking it.
        var stylesheet = ReadAsset(Path.Combine(webRoot, "wiki", "wiki.css"));
        var favicon = ReadAsset(Path.Combine(webRoot, "assets", "icons", "venta-mark.svg"));

        wiki.MapGet("/wiki.css", (HttpContext context) =>
            Asset(context, stylesheet, "text/css; charset=utf-8"));

        wiki.MapGet("/favicon.svg", (HttpContext context) =>
            Asset(context, favicon, "image/svg+xml"));

        // Indexing is a separate opt-in that does not exist yet, so nothing here is crawlable.
        wiki.MapGet("/robots.txt", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text("User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8");
        });

        // On a wiki's own hostname this is its index. On the apex it is deliberately nothing: a list
        // of published wikis is the enumeration the slug routing exists to avoid.
        wiki.MapGet("/", async (HttpContext context, WikiServiceClient client) =>
        {
            if (SlugOf(context, host) is not { } slug)
            {
                await WriteAsync(context, WikiPageRenderer.NotFound(RenderContext(host)),
                    StatusCodes.Status404NotFound);
                return;
            }

            var result = await client.GetWikiAsync(slug, context.RequestAborted);
            if (result.Value is not { } found) await WriteMissingAsync(context, host, result.ServiceReachable);
            else await WritePageAsync(context, WikiPageRenderer.Index(found, RenderContext(host)));
        });

        wiki.MapGet("/{first}", async (string first, HttpContext context, WikiServiceClient client) =>
        {
            if (SlugOf(context, host) is not { } slug)
            {
                await RedirectToWikiAsync(context, host, first, null);
                return;
            }

            var result = await client.GetPageAsync(slug, first, context.RequestAborted);
            if (result.Value is not { } found) await WriteMissingAsync(context, host, result.ServiceReachable);
            else await WritePageAsync(context, WikiPageRenderer.Page(found, RenderContext(host)));
        });

        // Only the apex ever had a two-segment form. Kept as a redirect rather than deleted: it
        // costs nothing, every link already posted uses it, and it is the address that still serves
        // if the wildcard certificate is not ready.
        wiki.MapGet("/{first}/{second}", async (
            string first, string second, HttpContext context) =>
        {
            if (SlugOf(context, host) is not null)
            {
                await WriteAsync(context, WikiPageRenderer.NotFound(RenderContext(host)),
                    StatusCodes.Status404NotFound);
                return;
            }

            await RedirectToWikiAsync(context, host, first, second);
        });

        return app;
    }

    /// <summary>
    /// Answers the reverse proxy's on-demand TLS question. A wildcard certificate needs a DNS
    /// challenge and a provider credential the bundled proxy does not have, so each published wiki's
    /// certificate is issued on first request instead - and this is what stops that becoming an
    /// unbounded issuance target for anyone who can point a name at the host.
    /// </summary>
    private static void MapCertificateCheck(WebApplication app, string host)
    {
        // Deliberately outside the host branch: the proxy asks over the container network with its
        // own Host header, and the answer is about a name it has been given rather than about the
        // name it asked on.
        app.MapGet(CertificateCheckPath, async (
            string? domain, HttpContext context, WikiServiceClient client) =>
        {
            var match = WikiHost.Match(domain, host);
            if (!match.IsWikiHost) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // The apex is configured, not published, so it never needs asking about.
            if (match.Slug is not { } slug) return Results.Ok();

            var result = await client.GetWikiAsync(slug, context.RequestAborted);

            // A timeout must not be read as "no such wiki": the proxy caches a refusal, and a
            // certificate refused during a Guild restart would keep failing long after it recovered.
            if (!result.ServiceReachable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            return result.Found ? Results.Ok() : Results.NotFound();
        }).RequireRateLimiting(WikiRateLimiting.PolicyName);
    }

    /// <summary>The wiki the request's hostname names, or null on the apex.</summary>
    private static string? SlugOf(HttpContext context, string host) =>
        WikiHost.Match(context.Request.Host.Host, host).Slug;

    /// <summary>Sends the apex's path form to the wiki's own hostname, permanently.</summary>
    private static Task RedirectToWikiAsync(HttpContext context, string host, string slug, string? pageSlug)
    {
        // A slug that could not be a hostname is a slug nothing published, and emitting a Location
        // header pointing at an unresolvable name is worse than saying so.
        if (!WikiHost.IsLabel(slug))
        {
            return WriteAsync(context, WikiPageRenderer.NotFound(RenderContext(host)),
                StatusCodes.Status404NotFound);
        }

        var path = pageSlug is null ? "/" : $"/{Uri.EscapeDataString(pageSlug)}";
        var target = $"{SiteHost.Scheme}://{WikiHost.HostFor(slug, host)}{path}{context.Request.QueryString}";

        context.Response.Headers.CacheControl = $"public, max-age={PageCacheSeconds}";
        context.Response.Redirect(target, permanent: true);

        return Task.CompletedTask;
    }

    /// <summary>Everything the renderer needs, rebuilt per request so a config reload is picked up.</summary>
    private static WikiRenderContext RenderContext(string host) => new(
        Scheme: SiteHost.Scheme,
        ApexHost: host,
        Links: new WikiLinkPolicy(WikiSiteSecurity.ImageHosts, Env.GeneralConfiguration.InstanceBaseUrl),
        SupportUrl: $"{SiteHost.BaseUrl(SiteHosting.SupportHost)}/contact",
        InstanceName: Env.Federation.InstanceName);

    private static Task WritePageAsync(HttpContext context, string html)
    {
        context.Response.Headers.CacheControl = $"public, max-age={PageCacheSeconds}";
        return WriteAsync(context, html, StatusCodes.Status200OK);
    }

    /// <summary>
    /// A slug nobody published, a page nobody published and a guild that no longer exists are the
    /// same answer. A service that did not answer is not, because "gone" is a claim this must not
    /// make on the strength of a timeout.
    /// </summary>
    private static Task WriteMissingAsync(HttpContext context, string host, bool serviceReachable) =>
        serviceReachable
            ? WriteAsync(context, WikiPageRenderer.NotFound(RenderContext(host)), StatusCodes.Status404NotFound)
            : WriteAsync(context, WikiPageRenderer.Unavailable(RenderContext(host)), StatusCodes.Status503ServiceUnavailable);

    private static async Task WriteAsync(HttpContext context, string html, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private static IResult Asset(HttpContext context, string? content, string contentType)
    {
        if (content is null) return Results.NotFound();

        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Text(content, contentType);
    }

    private static string? ReadAsset(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
