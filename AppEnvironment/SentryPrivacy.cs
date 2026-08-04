using System.Security.Cryptography;
using System.Text;
using Sentry;
using Sentry.Extensibility;

namespace AppEnvironment;

/// <summary>The consent gate in front of error reporting (T0-4 of docs/specs/privacy.md).</summary>
public static class SentryPrivacy
{
    /// <summary>
    /// The keys removed from an event's tags, extra data and breadcrumb data wherever they appear.
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
    /// </summary>
    public static Func<string?, bool> HasDataCollectionConsent { get; set; } = static _ => false;

    /// <summary>
    /// A random value generated once per process, used to key the pseudonymization HMAC.
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
    /// </summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        var user = sentryEvent.User;
        var consented = HasDataCollectionConsent(user.Id);
        if (consented) return sentryEvent;

        // The id is replaced rather than removed.
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

    /// <summary>Strips PII from a breadcrumb.</summary>
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

    /// <summary>True when a key name looks like it carries personal data.</summary>
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
