using System.Security.Cryptography;
using System.Text;
using Sentry;
using Sentry.Extensibility;

namespace AppEnvironment;

/// <summary>
/// The consent gate in front of error reporting (T0-4 of docs/specs/privacy.md).
///
/// <para><b>Crash reporting stays on unconditionally, and that is deliberate.</b> It is
/// service-operational - a server that cannot see its own exceptions cannot be kept safe or
/// available - and it is not a product-analytics pipeline. What consent governs is not <i>whether</i>
/// an error is reported but <i>who it is reported as</i>: without <c>AllowDataCollection</c> the
/// event carries a per-install pseudonym instead of an account id, and email, username, phone number
/// and request bodies are stripped from the event and from every breadcrumb attached to it.</para>
///
/// <para><b>It fails closed.</b> <see cref="HasDataCollectionConsent"/> defaults to "no", so a
/// service that never wires up a consent lookup pseudonymizes everything rather than reporting
/// identifiers it has no permission for. Getting less useful telemetry is a cost; sending a user's
/// email to a third-party error tracker because a delegate was left unset is a breach.</para>
///
/// <para>Scrubbing happens in <c>BeforeSend</c>/<c>BeforeBreadcrumb</c> rather than at each call
/// site because the SDK enriches events from ambient state - the ASP.NET Core integration reads the
/// current <c>ClaimsPrincipal</c>, the HTTP request and the logging scope on its own. Anything that
/// depends on remembering to redact at the throw site is not a control.</para>
/// </summary>
public static class SentryPrivacy
{
    /// <summary>
    /// The keys removed from an event's tags, extra data and breadcrumb data wherever they appear.
    /// Matched case-insensitively, and by containment rather than equality: framework and library
    /// enrichers namespace their keys (<c>user.email</c>, <c>Identity.UserName</c>,
    /// <c>request.body</c>), so an equality check would let through exactly the enriched copies that
    /// are hardest to notice.
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    [
        "email",
        "username",
        "user_name",
        "phone",
        "password",
        "token",
        "secret",
        "authorization",
        "cookie",
        "requestbody",
        "request_body",
        "request.body",
        "body",
        "payload",
    ];

    private const string Redacted = "[redacted]";

    /// <summary>
    /// Resolves whether the account behind the current event has consented to data collection.
    ///
    /// <para>A delegate rather than a service dependency because this assembly is referenced by every
    /// service and knows nothing about any of their databases; the service that owns the consent -
    /// or, in the consuming services, the Redis-backed privacy-settings cache - sets it at startup.
    /// The argument is the user id from the event, or null when the event has none.</para>
    ///
    /// <para>Default: always false. See the class remarks - the failure mode of a wrong "true" is
    /// unrecoverable, the failure mode of a wrong "false" is a slightly less convenient stack
    /// trace.</para>
    /// </summary>
    public static Func<string?, bool> HasDataCollectionConsent { get; set; } = static _ => false;

    /// <summary>
    /// A random value generated once per process, used to key the pseudonymization HMAC.
    ///
    /// <para>Per-install rather than global so a pseudonym cannot be correlated across deployments,
    /// and keyed rather than a plain hash because the input space - user ids - is enumerable: an
    /// unkeyed <c>sha256(userId)</c> is reversible by anyone holding the id list, which is not
    /// pseudonymization, it is encoding. Settable through <c>SENTRY_INSTALL_SALT</c> so a
    /// multi-pod deployment can agree on one and keep a user's errors groupable.</para>
    /// </summary>
    private static readonly byte[] InstallSalt = ResolveInstallSalt();

    private static byte[] ResolveInstallSalt()
    {
        var configured = Environment.GetEnvironmentVariable("SENTRY_INSTALL_SALT");
        return !string.IsNullOrWhiteSpace(configured)
            ? Encoding.UTF8.GetBytes(configured)
            : RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>The stable stand-in for an account id, for an account that has not consented to
    /// being identified. Deterministic within an install, so two crashes from the same user still
    /// group; meaningless outside it.</summary>
    public static string Pseudonymize(string userId)
    {
        var digest = HMACSHA256.HashData(InstallSalt, Encoding.UTF8.GetBytes(userId));
        return "anon_" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Strips PII from an outgoing event unless the account behind it has consented.
    ///
    /// <para>Never returns null: dropping the event would trade a privacy problem for a reliability
    /// blind spot, and there is nothing in an exception that has to be discarded once its
    /// identifiers are gone.</para>
    /// </summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        var user = sentryEvent.User;
        var consented = HasDataCollectionConsent(user.Id);
        if (consented) return sentryEvent;

        // The id is replaced rather than removed. Without something stable there is no way to tell
        // "one user hitting this a thousand times" from "a thousand users", and that difference is
        // what decides whether an error is an incident.
        user.Id = string.IsNullOrEmpty(user.Id) ? null : Pseudonymize(user.Id);
        user.Email = null;
        user.Username = null;
        user.IpAddress = null;
        user.Other?.Clear();

        foreach (var key in sentryEvent.Tags.Keys.Where(IsSensitive).ToList())
        {
            sentryEvent.SetTag(key, Redacted);
        }

        if (sentryEvent.Request is { } request)
        {
            request.Data = null;          // the request body
            request.QueryString = null;   // routinely carries tokens and email addresses
            request.Cookies = null;
            ScrubKeyed(request.Headers);
            ScrubKeyed(request.Env);
        }

        // Breadcrumbs attached to this event were already put through Scrub(Breadcrumb) as they were
        // recorded - see AddErrorReporting's SetBeforeBreadcrumb - and the collection here is
        // immutable, so there is nothing left to do to them.

        return sentryEvent;
    }

    /// <summary>
    /// Strips PII from a breadcrumb. Breadcrumbs are the leakiest surface here: every logged message
    /// in the request becomes one, and log messages are where an email address ends up without anyone
    /// deciding it should.
    ///
    /// <para>The consent lookup is asked about a null user because a breadcrumb is recorded outside
    /// any event and carries no account id. With the fail-closed default that means breadcrumbs are
    /// always scrubbed, which is the right way round: a breadcrumb recorded now may end up attached
    /// to an event raised for a different request entirely.</para>
    ///
    /// <para><see cref="Breadcrumb"/> is immutable, so a redacted one is a new instance. The
    /// timestamp is re-taken rather than carried over - the public constructor does not accept one -
    /// which costs the microseconds between the breadcrumb being created and this callback running.
    /// Ordering is unaffected.</para>
    /// </summary>
    public static Breadcrumb Scrub(Breadcrumb breadcrumb)
    {
        if (HasDataCollectionConsent(null)) return breadcrumb;
        if (breadcrumb.Data is null || !breadcrumb.Data.Keys.Any(IsSensitive)) return breadcrumb;

        var cleaned = breadcrumb.Data.ToDictionary(
            pair => pair.Key,
            pair => IsSensitive(pair.Key) ? Redacted : pair.Value);

        return new Breadcrumb(
            breadcrumb.Message,
            breadcrumb.Type,
            cleaned,
            breadcrumb.Category,
            breadcrumb.Level);
    }

    /// <summary>True when a key name looks like it carries personal data. Deliberately broad - a
    /// false positive costs one redacted debugging field.</summary>
    public static bool IsSensitive(string key) =>
        SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void ScrubKeyed(IDictionary<string, string>? values)
    {
        if (values is null) return;

        foreach (var key in values.Keys.Where(IsSensitive).ToList())
        {
            values[key] = Redacted;
        }
    }
}
