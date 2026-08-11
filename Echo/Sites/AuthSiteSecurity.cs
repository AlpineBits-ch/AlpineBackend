namespace Echo.Sites;

/// <summary>Security headers for the sign-in host, and nowhere else.</summary>
public static class AuthSiteSecurity
{
    /// <summary>
    /// <c>'unsafe-inline'</c> appears nowhere, which is a constraint on the pages rather than a
    /// setting: no inline <c>&lt;script&gt;</c>, no <c>style=""</c> attributes, no <c>onclick</c>.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; "
        + "script-src 'self'; "
        + "style-src 'self'; "
        + "img-src 'self' data: https://*.steamstatic.com; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'";

    public static IApplicationBuilder UseAuthSiteSecurity(this IApplicationBuilder branch)
    {
        branch.Use(async (context, next) =>
        {
            // Set on the response before anything writes to it: headers added after the first byte
            // has gone out are silently dropped, and static files start streaming early.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XFrameOptions = "DENY";
                headers.XContentTypeOptions = "nosniff";

                // Keeps the parked-request id and any login_hint out of onward requests.
                headers["Referrer-Policy"] = "no-referrer";

                // A sign-in page cached by an intermediary is a sign-in page served to the next
                // person on the same connection, and every response here that is not a static asset
                // is either a document about who somebody is or a token exchange.
                if (!IsStaticAsset(context)) headers.CacheControl = "no-store";

                return Task.CompletedTask;
            });

            await next();
        });

        return branch;
    }

    /// <summary>A file that ships inside the image and says nothing about anybody.</summary>
    private static bool IsStaticAsset(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return false;

        return path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);
    }
}
