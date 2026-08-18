using AppEnvironment;

namespace Echo.Sites;

/// <summary>Security headers for the public wiki host, and nowhere else.</summary>
public static class WikiSiteSecurity
{
    /// <summary>The origins a page's images may load from: this host, plus the instance's media store.</summary>
    public static IReadOnlyList<string> ImageOrigins { get; } = BuildImageOrigins();

    /// <summary>The hostnames behind <see cref="ImageOrigins"/>, for the markdown renderer.</summary>
    public static IReadOnlyList<string> ImageHosts { get; } = BuildImageHosts();

    /// <summary>
    /// Stricter than the sign-in site's, because this host renders prose written by anyone holding
    /// CreateWikiPages in any guild on the instance. There is no <c>script-src 'self'</c>: the pages
    /// are rendered server-side and ship no script, so a policy that permits none is a policy that
    /// costs nothing and turns any escaped markup into inert text.
    /// </summary>
    // Declared after the two lists it reads: static initialisers run in declaration order, and
    // building the policy first read a null allowlist and threw out of the type initialiser.
    public static string ContentSecurityPolicy { get; } = BuildPolicy();

    public static IApplicationBuilder UseWikiSiteSecurity(this IApplicationBuilder branch)
    {
        branch.Use(async (context, next) =>
        {
            // Set before anything writes, the same as the sign-in site: headers added after the
            // first byte has gone out are dropped, and static files start streaming early.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XFrameOptions = "DENY";
                headers.XContentTypeOptions = "nosniff";

                // A public page must not carry its reader's referrer to whatever a page author
                // linked to.
                headers["Referrer-Policy"] = "no-referrer";

                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

                // Belt and braces with the meta tag: indexing is a separate opt-in that does not
                // exist yet, and a header covers responses a crawler reads without parsing.
                headers["X-Robots-Tag"] = "noindex, nofollow";

                return Task.CompletedTask;
            });

            await next();
        });

        return branch;
    }

    private static string BuildPolicy() =>
        "default-src 'none'; "
        + "script-src 'none'; "
        + "style-src 'self'; "
        + $"img-src {string.Join(' ', ImageOrigins)}; "
        + "font-src 'self'; "
        + "connect-src 'none'; "
        + "form-action 'none'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'";

    private static List<string> BuildImageOrigins()
    {
        var origins = new List<string> { "'self'" };

        // The instance origin belongs here as well as the store's: an app-relative cover is rebased
        // onto it, because a leading slash resolves against the wiki host, which serves no media.
        foreach (var candidate in new[] { Env.GeneralConfiguration.InstanceUrl, Env.StorageConfiguration.PublicUrl })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                var origin = $"{uri.Scheme}://{uri.Authority}";
                if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) origins.Add(origin);
            }
        }

        return origins;
    }

    private static List<string> BuildImageHosts()
    {
        var hosts = new List<string>();

        foreach (var candidate in new[] { Env.GeneralConfiguration.InstanceUrl, Env.StorageConfiguration.PublicUrl })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                hosts.Add(uri.Host);
        }

        return hosts;
    }
}
