using AppEnvironment;
using Sentry;

namespace Identity.Tests.Services;

/// <summary>
/// T0-4: the consent gate in front of error reporting.
///
/// <para>Crash reporting is service-operational and stays on for everyone; what
/// <c>AllowDataCollection</c> governs is whether the event carries an account identifier or a
/// per-install pseudonym, and whether email, username, phone number and request bodies survive the
/// trip to a third-party error tracker.</para>
///
/// <para>The load-bearing test is <see cref="Scrub_WithNoConsentResolverWiredUp_StillScrubs"/>: the
/// gate defaults to "no consent", so a service that forgets to wire up a lookup pseudonymizes
/// everything rather than reporting identifiers it has no permission for.</para>
/// </summary>
[TestFixture]
public class SentryPrivacyTests
{
    private Func<string?, bool> _original = null!;

    [SetUp]
    public void SetUp() => _original = SentryPrivacy.HasDataCollectionConsent;

    [TearDown]
    public void TearDown() => SentryPrivacy.HasDataCollectionConsent = _original;

    private static SentryEvent EventWithPii(string userId = "user_123")
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("boom"));
        sentryEvent.User.Id = userId;
        sentryEvent.User.Email = "someone@example.com";
        sentryEvent.User.Username = "someone";
        sentryEvent.User.IpAddress = "203.0.113.7";
        sentryEvent.SetTag("user.email", "someone@example.com");
        sentryEvent.SetTag("phoneNumber", "+41790000000");
        sentryEvent.SetTag("channelId", "chan_1");
        sentryEvent.Request.Data = """{"password":"hunter2"}""";
        sentryEvent.Request.QueryString = "email=someone@example.com";
        sentryEvent.Request.Cookies = "session=abc";
        return sentryEvent;
    }

    // ── negative: no consent (the default) ──────────────────────────────────

    [Test]
    public void Scrub_WithoutConsent_RemovesEveryDirectIdentifier()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var scrubbed = SentryPrivacy.Scrub(EventWithPii());

        Assert.Multiple(() =>
        {
            Assert.That(scrubbed.User.Email, Is.Null);
            Assert.That(scrubbed.User.Username, Is.Null);
            Assert.That(scrubbed.User.IpAddress, Is.Null);
            Assert.That(scrubbed.Request.Data, Is.Null, "request bodies carry messages and credentials");
            Assert.That(scrubbed.Request.QueryString, Is.Null);
            Assert.That(scrubbed.Request.Cookies, Is.Null);
        });
    }

    [Test]
    public void Scrub_WithoutConsent_ReplacesTheUserIdWithAStablePseudonym()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var first = SentryPrivacy.Scrub(EventWithPii()).User.Id;
        var second = SentryPrivacy.Scrub(EventWithPii()).User.Id;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null.And.Not.EqualTo("user_123"));
            Assert.That(first, Does.StartWith("anon_"));
            Assert.That(second, Is.EqualTo(first),
                "without something stable there is no way to tell one user hitting an error a "
                + "thousand times from a thousand users hitting it once");
        });
    }

    [Test]
    public void Scrub_WithoutConsent_GivesDifferentUsersDifferentPseudonyms()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var a = SentryPrivacy.Scrub(EventWithPii("user_a")).User.Id;
        var b = SentryPrivacy.Scrub(EventWithPii("user_b")).User.Id;

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Scrub_WithoutConsent_RedactsSensitiveTagsButKeepsOperationalOnes()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var scrubbed = SentryPrivacy.Scrub(EventWithPii());

        Assert.Multiple(() =>
        {
            Assert.That(scrubbed.Tags["user.email"], Is.EqualTo("[redacted]"));
            Assert.That(scrubbed.Tags["phoneNumber"], Is.EqualTo("[redacted]"));
            Assert.That(scrubbed.Tags["channelId"], Is.EqualTo("chan_1"),
                "scrubbing must not cost the fields that make an error diagnosable");
        });
    }

    [Test]
    public void Scrub_WithNoConsentResolverWiredUp_StillScrubs()
    {
        // The default, restored by TearDown, and the only behaviour that matters if a service's
        // startup never sets a resolver: a wrong "true" here is unrecoverable, a wrong "false" costs
        // a slightly less convenient stack trace.
        SentryPrivacy.HasDataCollectionConsent = SentryPrivacyDefault();

        var scrubbed = SentryPrivacy.Scrub(EventWithPii());

        Assert.Multiple(() =>
        {
            Assert.That(scrubbed.User.Email, Is.Null);
            Assert.That(scrubbed.User.Id, Does.StartWith("anon_"));
        });
    }

    [Test]
    public void Scrub_EventWithNoUser_DoesNotInventOne()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var sentryEvent = new SentryEvent(new InvalidOperationException("boom"));

        var scrubbed = SentryPrivacy.Scrub(sentryEvent);

        Assert.That(scrubbed.User.Id, Is.Null, "there was nothing to pseudonymize");
    }

    [Test]
    public void Scrub_NeverDropsTheEvent()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        Assert.That(SentryPrivacy.Scrub(EventWithPii()), Is.Not.Null,
            "trading a privacy problem for a reliability blind spot is not the deal - the event goes "
            + "out, without the identifiers");
    }

    // ── normal: consent given ───────────────────────────────────────────────

    [Test]
    public void Scrub_WithConsent_LeavesTheEventAlone()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => true;

        var scrubbed = SentryPrivacy.Scrub(EventWithPii());

        Assert.Multiple(() =>
        {
            Assert.That(scrubbed.User.Id, Is.EqualTo("user_123"));
            Assert.That(scrubbed.User.Email, Is.EqualTo("someone@example.com"));
        });
    }

    [Test]
    public void Scrub_ConsentIsResolvedPerUser()
    {
        SentryPrivacy.HasDataCollectionConsent = id => id == "user_yes";

        Assert.That(SentryPrivacy.Scrub(EventWithPii("user_yes")).User.Email,
            Is.EqualTo("someone@example.com"));
        Assert.That(SentryPrivacy.Scrub(EventWithPii("user_no")).User.Email, Is.Null);
    }

    // ── breadcrumbs ─────────────────────────────────────────────────────────

    [Test]
    public void Scrub_Breadcrumb_RedactsSensitiveDataKeys()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var breadcrumb = new Breadcrumb(
            message: "login attempt",
            type: "auth",
            data: new Dictionary<string, string>
            {
                ["email"] = "someone@example.com",
                ["requestBody"] = """{"password":"hunter2"}""",
                ["outcome"] = "failed",
            });

        var scrubbed = SentryPrivacy.Scrub(breadcrumb);

        Assert.Multiple(() =>
        {
            Assert.That(scrubbed.Data!["email"], Is.EqualTo("[redacted]"));
            Assert.That(scrubbed.Data["requestBody"], Is.EqualTo("[redacted]"));
            Assert.That(scrubbed.Data["outcome"], Is.EqualTo("failed"));
            Assert.That(scrubbed.Message, Is.EqualTo("login attempt"));
            Assert.That(scrubbed.Category, Is.EqualTo(breadcrumb.Category));
        });
    }

    [Test]
    public void Scrub_BreadcrumbWithNothingSensitive_IsReturnedUnchanged()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var breadcrumb = new Breadcrumb(message: "cache miss", type: "cache", data: new Dictionary<string, string>
        {
            ["key"] = "privacy_settings:user_id:user_1",
        });

        var scrubbed = SentryPrivacy.Scrub(breadcrumb);

        Assert.That(scrubbed.Timestamp, Is.EqualTo(breadcrumb.Timestamp),
            "an untouched breadcrumb keeps its identity, timestamp included");
    }

    [Test]
    public void Scrub_BreadcrumbWithNoData_IsReturnedUnchanged()
    {
        SentryPrivacy.HasDataCollectionConsent = _ => false;

        var breadcrumb = new Breadcrumb(message: "startup", type: "default");

        Assert.That(SentryPrivacy.Scrub(breadcrumb).Message, Is.EqualTo("startup"));
    }

    // ── the key classifier ──────────────────────────────────────────────────

    [TestCase("email")]
    [TestCase("Email")]
    [TestCase("user.email")]
    [TestCase("UserName")]
    [TestCase("user_name")]
    [TestCase("phoneNumber")]
    [TestCase("Authorization")]
    [TestCase("request.body")]
    [TestCase("PasswordHash")]
    public void IsSensitive_MatchesEnrichedKeyNamesToo(string key)
    {
        // Containment rather than equality on purpose: framework and library enrichers namespace
        // their keys, and an equality check would let through exactly the enriched copies that are
        // hardest to notice.
        Assert.That(SentryPrivacy.IsSensitive(key), Is.True);
    }

    [TestCase("channelId")]
    [TestCase("guildId")]
    [TestCase("statusCode")]
    [TestCase("elapsedMs")]
    public void IsSensitive_LeavesOperationalKeysAlone(string key)
    {
        Assert.That(SentryPrivacy.IsSensitive(key), Is.False);
    }

    /// <summary>The shipped default of <see cref="SentryPrivacy.HasDataCollectionConsent"/>, stated
    /// here so the test asserts the contract rather than whatever the field happens to hold once
    /// another test has run.</summary>
    private static Func<string?, bool> SentryPrivacyDefault() => static _ => false;
}
