using Microsoft.Extensions.FileProviders;

namespace Echo.Sites;

/// <summary>
/// Serves the admin console, the support site and the status page from the gateway, each on its own
/// hostname and nowhere else.
/// </summary>
public static class SiteHosting
{
    public const string AdminLabel = "admin";
    public const string SupportLabel = "support";
    public const string StatusLabel = "status";

    public const string AdminDomainVariable = "ADMIN_DOMAIN";
    public const string SupportDomainVariable = "SUPPORT_DOMAIN";
    public const string StatusDomainVariable = "STATUS_DOMAIN";

    public static string AdminHost => SiteHost.Resolve(AdminLabel, AdminDomainVariable);
    public static string SupportHost => SiteHost.Resolve(SupportLabel, SupportDomainVariable);
    public static string StatusHost => SiteHost.Resolve(StatusLabel, StatusDomainVariable);

    /// <summary>Serves both sites, plus the shared icon set.</summary>
    public static WebApplication MapVentaSites(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath ?? "wwwroot";

        var admin = AdminHost;
        var support = SupportHost;
        var status = StatusHost;

        app.Logger.LogInformation(
            "Moderation console bound to host {AdminHost}; support site bound to {SupportHost}; status page bound to {StatusHost}",
            admin, support, status);

        app.UseSiteHostDiagnostics(AdminLabel, admin, AdminDomainVariable, "moderation console");
        app.UseSiteHostDiagnostics(SupportLabel, support, SupportDomainVariable, "support site");
        app.UseSiteHostDiagnostics(StatusLabel, status, StatusDomainVariable, "status page");

        var icons = Path.Combine(webRoot, "assets");
        var iconProvider = Directory.Exists(icons) ? new PhysicalFileProvider(icons) : null;

        // The console routes on the fragment (#queue, #appeals), which never reaches the server, so
        // it needs no fallback.
        app.ServeSite(Path.Combine(webRoot, "admin"), admin, iconProvider);
        app.ServeSite(Path.Combine(webRoot, "support"), support, iconProvider, SupportClientRoutes);
        app.ServeSite(Path.Combine(webRoot, "status"), status, iconProvider, StatusClientRoutes);

        return app;
    }

    /// <summary>Paths on the status host that are pages rather than files.</summary>
    private static readonly string[] StatusClientRoutes = ["/incident", "/history", "/maintenance"];

    /// <summary>Paths on the support host that are pages rather than files.</summary>
    private static readonly string[] SupportClientRoutes = ["/contact", "/appeal", "/ticket"];

    private static void ServeSite(
        this WebApplication app, string root, string host, IFileProvider? iconProvider,
        string[]? clientRoutes = null)
    {
        if (!Directory.Exists(root))
        {
            app.Logger.LogWarning("Site assets not found at {Root}; {Host} will 404", root, host);
            return;
        }

        var files = new PhysicalFileProvider(root);

        app.UseWhen(
            context => context.Request.Host.Host.Equals(host, StringComparison.OrdinalIgnoreCase),
            branch =>
            {
                if (clientRoutes is { Length: > 0 })
                {
                    // Rewritten before the static-file middleware rather than added as a fallback
                    // endpoint after it: UseStaticFiles is middleware, so by the time routing would
                    // pick a fallback the request has already fallen through to YARP's catch-all.
                    branch.Use(async (context, next) =>
                    {
                        var path = context.Request.Path.Value?.TrimEnd('/');

                        if (!string.IsNullOrEmpty(path)
                            && HttpMethods.IsGet(context.Request.Method)
                            && clientRoutes.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            // Only the path is rewritten.
                            context.Request.Path = "/index.html";
                        }

                        await next();
                    });
                }

                branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });

                if (iconProvider is not null)
                {
                    // Mounted before the site's own files so a page cannot shadow /icons with a
                    // stale local copy.
                    branch.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = iconProvider,
                        RequestPath = "/assets",
                        OnPrepareResponse = ctx =>
                            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400",
                    });
                }

                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    OnPrepareResponse = ctx =>
                    {
                        var name = ctx.File.Name;

                        // The pages are hand-written and shipped inside the image, so they change
                        // only on deploy - but they have no content hash in their names either, so a
                        // long cache would strand an operator on yesterday's console. Revalidate.
                        ctx.Context.Response.Headers.CacheControl =
                            name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                ? "no-cache"
                                : "public, max-age=3600";
                    },
                });
            });
    }

    /// <summary>
    /// Answers a request for the wrong <c>label.*</c> hostname with an explanation instead of a
    /// silent 404.
    /// </summary>
    public static IApplicationBuilder UseSiteHostDiagnostics(
        this WebApplication app, string label, string boundHost, string variable, string description)
    {
        var prefix = $"{label}.";

        app.Use(async (context, next) =>
        {
            var requested = context.Request.Host.Host;

            if (requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !requested.Equals(boundHost, StringComparison.OrdinalIgnoreCase))
            {
                app.Logger.LogWarning(
                    "Request for {Requested} but the {Description} is bound to {Bound} (derived from "
                    + "INSTANCE_URL={InstanceUrl}). Set {Variable}={Requested} to serve it.",
                    requested, description, boundHost,
                    AppEnvironment.Env.GeneralConfiguration.InstanceUrl, variable, requested);

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    $"The {description} is served on '{boundHost}', not '{requested}'.\n"
                    + $"Set {variable}={requested} on the gateway to serve it here.\n");
                return;
            }

            await next();
        });

        return app;
    }
}
