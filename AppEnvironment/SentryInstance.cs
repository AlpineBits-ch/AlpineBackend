using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Sentry.Extensibility;

namespace AppEnvironment;

public static class SentryInstance
{
    /// <summary>Wires up error reporting with the T0-4 consent gate already applied.</summary>
    public static WebApplicationBuilder AddErrorReporting(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = Env.SentryUrl;
            o.Debug = true;

            // The SDK's own PII switch.
            o.SendDefaultPii = false;

            // Request bodies never leave the process.
            o.MaxRequestBodySize = RequestSize.None;

            o.SetBeforeSend(SentryPrivacy.Scrub);
            o.SetBeforeBreadcrumb(SentryPrivacy.Scrub);
        });
        return builder;
    }
}
