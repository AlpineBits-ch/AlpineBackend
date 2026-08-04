using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Sentry.Extensibility;

namespace AppEnvironment;

public static class SentryInstance
{
    /// <summary>
    /// Wires up error reporting with the T0-4 consent gate already applied.
    ///
    /// <para>Error reporting is service-operational and stays on for every account. What the gate
    /// governs is the identifiers it carries: see <see cref="SentryPrivacy"/>, which pseudonymizes
    /// the user id and strips email, username, phone number and request bodies from events and
    /// breadcrumbs unless <c>AllowDataCollection</c> is set - and which defaults to "not set".</para>
    /// </summary>
    public static WebApplicationBuilder AddErrorReporting(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = Env.SentryUrl;
            o.Debug = true;

            // The SDK's own PII switch. Off means it does not attach the ClaimsPrincipal's claims,
            // the client IP or cookies on its own - so the scrubbing below is a second line rather
            // than the only one. Turning this on would defeat the gate no matter what BeforeSend does,
            // because the enrichment happens on a copy the callback never sees for every transport.
            o.SendDefaultPii = false;

            // Request bodies never leave the process. There is no size at which sending the body of a
            // failed request to a third-party error tracker is acceptable here: these are messages,
            // credentials and key material.
            o.MaxRequestBodySize = RequestSize.None;

            o.SetBeforeSend(SentryPrivacy.Scrub);
            o.SetBeforeBreadcrumb(SentryPrivacy.Scrub);
        });
        return builder;
    }
}
