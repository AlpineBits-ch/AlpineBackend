using Identity.Application.Consumers;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Push;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ContractKind = Identity.Contracts.Enums.PushTokenKind;

namespace Identity.Tests.Push;

/// <summary>
/// The storage half of Web Push: a browser subscription is a triple, and it has to fit a table
/// built for one string.
/// </summary>
[TestFixture]
public class WebPushRegistrationTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";
    private const string P256dh = "p256dh-value";
    private const string Auth = "auth-value";

    private UserPushToken Seed(
        string userId = "user-1",
        string token = Endpoint,
        PushTokenKind kind = PushTokenKind.WebPush,
        string? p256dh = P256dh,
        string? auth = Auth)
    {
        var row = UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = userId,
            Token = token,
            Kind = kind,
            P256dh = p256dh,
            Auth = auth,
        });
        _context.UserPushTokens.Add(row);
        return row;
    }

    // ══════════════════════════════════════════════════════════════════════════ The entity
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The name a Web Push caller needs, over the column a send addresses.</summary>
    [Test]
    public void A_web_push_row_reports_its_endpoint()
    {
        Assert.That(Seed().Endpoint, Is.EqualTo(Endpoint));
    }

    /// <summary>Null for the other transports, so a caller cannot mistake an FCM token for a URL to POST
    /// to - which would be an outbound request to whatever that string happens to parse as.</summary>
    [Test]
    public void An_fcm_row_reports_no_endpoint()
    {
        Assert.That(Seed(kind: PushTokenKind.Fcm, p256dh: null, auth: null).Endpoint, Is.Null);
    }

    /// <summary>A row that cannot be encrypted to is not sendable, and the sender skips it rather than
    /// attempting an encryption with a null key.</summary>
    [TestCase(null, Auth)]
    [TestCase(P256dh, null)]
    [TestCase(null, null)]
    [TestCase("", Auth)]
    public void A_web_push_row_missing_a_key_is_incomplete(string? p256dh, string? auth)
    {
        Assert.That(Seed(p256dh: p256dh, auth: auth).IsComplete, Is.False);
    }

    /// <summary>FCM and APNs rows have no such keys and must not be judged by them - otherwise adding
    /// this concept would have marked every existing token unsendable.</summary>
    [TestCase(PushTokenKind.Fcm)]
    [TestCase(PushTokenKind.ApnsVoip)]
    public void A_row_of_another_kind_is_complete_without_keys(PushTokenKind kind)
    {
        Assert.That(Seed(kind: kind, p256dh: null, auth: null).IsComplete, Is.True);
    }

    /// <summary>The keys are re-read on re-registration, not just the owner.</summary>
    [Test]
    public void Re_registering_refreshes_the_subscription_keys()
    {
        var row = Seed();

        row.ReassignTo("user-2", null, "new-p256dh", "new-auth");

        Assert.That(row.UserId, Is.EqualTo("user-2"));
        Assert.That(row.P256dh, Is.EqualTo("new-p256dh"));
        Assert.That(row.Auth, Is.EqualTo("new-auth"));
    }

    /// <summary>An FCM re-registration passes nulls and must not blank the columns it does not use.</summary>
    [Test]
    public void Reassigning_without_keys_leaves_the_stored_ones_alone()
    {
        var row = Seed();

        row.ReassignTo("user-2", null, null, null);

        Assert.That(row.P256dh, Is.EqualTo(P256dh));
        Assert.That(row.Auth, Is.EqualTo(Auth));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Validation - Identity.Contracts.Push.WebPushSubscription
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A real subscription, to prove the validator is not simply refusing everything.</summary>
    [Test]
    public void A_well_formed_subscription_passes()
    {
        var (p256dh, auth) = RealSubscription();

        Assert.That(WebPushSubscription.Validate(Endpoint, p256dh, auth), Is.Null);
    }

    [TestCase(null, TestName = "a missing endpoint is refused")]
    [TestCase("", TestName = "an empty endpoint is refused")]
    [TestCase("/fcm/send/abc", TestName = "a relative endpoint is refused")]
    // http, not https: the server POSTs to this value, so accepting it means an unencrypted outbound
    // call carrying an encrypted payload to somewhere unverified.
    [TestCase("http://fcm.googleapis.com/fcm/send/abc", TestName = "a plaintext endpoint is refused")]
    public void An_unusable_endpoint_is_refused(string? endpoint)
    {
        var (p256dh, auth) = RealSubscription();

        Assert.That(WebPushSubscription.Validate(endpoint, p256dh, auth), Is.Not.Null);
    }

    /// <summary>The lengths are the specified ones.</summary>
    [Test]
    public void A_p256dh_of_the_wrong_length_is_refused()
    {
        var (_, auth) = RealSubscription();
        var short64 = WebPushSubscription.Encode(new byte[64]);

        Assert.That(WebPushSubscription.Validate(Endpoint, short64, auth), Is.Not.Null);
    }

    /// <summary>A compressed point (0x02/0x03) is a valid P-256 encoding that the Push API does not
    /// produce; guessing at it would derive a key from a reconstructed point.</summary>
    [Test]
    public void A_compressed_p256dh_is_refused()
    {
        var (_, auth) = RealSubscription();
        var compressed = new byte[65];
        compressed[0] = 0x02;

        Assert.That(WebPushSubscription.Validate(Endpoint, WebPushSubscription.Encode(compressed), auth),
            Is.Not.Null);
    }

    [Test]
    public void An_auth_secret_of_the_wrong_length_is_refused()
    {
        var (p256dh, _) = RealSubscription();

        Assert.That(WebPushSubscription.Validate(Endpoint, p256dh, WebPushSubscription.Encode(new byte[15])),
            Is.Not.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    public void A_missing_key_is_refused(string? missing)
    {
        var (p256dh, auth) = RealSubscription();

        Assert.That(WebPushSubscription.Validate(Endpoint, missing, auth), Is.Not.Null);
        Assert.That(WebPushSubscription.Validate(Endpoint, p256dh, missing), Is.Not.Null);
    }

    /// <summary>A key that is not base64url at all is refused, not thrown over.</summary>
    [TestCase("short", TestName = "a length of 4n+1")]
    [TestCase("!!!!", TestName = "characters outside the alphabet")]
    [TestCase("AA=AAA", TestName = "padding in the middle")]
    [TestCase("A+/=", TestName = "the standard base64 alphabet, not base64url")]
    public void A_key_that_is_not_base64url_is_refused_rather_than_throwing(string malformed)
    {
        var (p256dh, auth) = RealSubscription();

        Assert.That(WebPushSubscription.TryDecode(malformed, out _), Is.False);
        Assert.That(WebPushSubscription.Validate(Endpoint, malformed, auth), Is.Not.Null);
        Assert.That(WebPushSubscription.Validate(Endpoint, p256dh, malformed), Is.Not.Null);
    }

    /// <summary>
    /// <c>PushSubscription.getKey()</c> values are base64url with the padding stripped, and
    /// <c>Convert.FromBase64String</c> rejects that outright - so a naive decode fails on every real
    /// subscription rather than on a malformed one.
    /// </summary>
    [Test]
    public void Unpadded_base64url_decodes()
    {
        // 16 bytes encodes to 22 unpadded characters; padded it would be 24 with "==".
        var encoded = WebPushSubscription.Encode(new byte[16]);

        Assert.That(encoded, Has.Length.EqualTo(22));
        Assert.That(encoded, Does.Not.Contain("="));
        Assert.That(WebPushSubscription.TryDecode(encoded, out var decoded), Is.True);
        Assert.That(decoded, Has.Length.EqualTo(16));
    }

    /// <summary>Base64url uses <c>-</c> and <c>_</c> where base64 uses <c>+</c> and <c>/</c>. A decoder
    /// that got this wrong would fail only on the subset of keys containing those bytes.</summary>
    [Test]
    public void Base64url_alphabet_round_trips()
    {
        var bytes = new byte[] { 0xFB, 0xFF, 0xBE, 0x3F, 0x00, 0xFF };
        var encoded = WebPushSubscription.Encode(bytes);

        Assert.That(encoded, Does.Not.Contain("+").And.Not.Contain("/"));
        Assert.That(WebPushSubscription.Decode(encoded), Is.EqualTo(bytes));
    }

    // ══════════════════════════════════════════════════════════════════════════ The lookup senders
    // use ══════════════════════════════════════════════════════════════════════════

    /// <summary>Without the keys on the response a sender would need a second round trip to Identity for
    /// two strings, per recipient, per notification.</summary>
    [Test]
    public async Task The_lookup_returns_the_subscription_keys()
    {
        Seed();
        await _context.SaveChangesAsync();

        var response = await GetPushTokensHandler.Handle(
            new GetPushTokensForUsersRequest { UserIds = ["user-1"] }, _context);

        var row = response.Tokens.Single();
        Assert.That(row.Kind, Is.EqualTo(ContractKind.WebPush));
        Assert.That(row.Token, Is.EqualTo(Endpoint));
        Assert.That(row.P256dh, Is.EqualTo(P256dh));
        Assert.That(row.Auth, Is.EqualTo(Auth));
    }

    /// <summary>The whole point of the new kind travelling on the wire.</summary>
    [Test]
    public async Task A_web_push_row_is_not_reported_as_fcm()
    {
        Seed();
        await _context.SaveChangesAsync();

        var response = await GetPushTokensHandler.Handle(
            new GetPushTokensForUsersRequest { UserIds = ["user-1"], Kinds = [ContractKind.Fcm] },
            _context);

        Assert.That(response.Tokens, Is.Empty, "an Fcm-only request must not return a browser endpoint");
    }

    [Test]
    public async Task A_web_push_row_is_returned_when_asked_for_by_kind()
    {
        Seed();
        await _context.SaveChangesAsync();

        var response = await GetPushTokensHandler.Handle(
            new GetPushTokensForUsersRequest { UserIds = ["user-1"], Kinds = [ContractKind.WebPush] },
            _context);

        Assert.That(response.Tokens.Single().Kind, Is.EqualTo(ContractKind.WebPush));
    }

    /// <summary>The decision the client contract asked the server to make.</summary>
    [Test]
    public async Task Silent_sends_exclude_browser_subscriptions()
    {
        Seed();
        Seed(token: "fcm-token", kind: PushTokenKind.Fcm, p256dh: null, auth: null);
        await _context.SaveChangesAsync();

        var response = await GetPushTokensHandler.Handle(
            new GetPushTokensForUsersRequest { UserIds = ["user-1"] }, _context);

        Assert.That(response.SilentCapable.Select(t => t.Token), Is.EquivalentTo(new[] { "fcm-token" }));
    }

    /// <summary>A row missing its keys is excluded from a send rather than attempted with a null
    /// key.</summary>
    [Test]
    public async Task Sendable_excludes_a_subscription_missing_its_keys()
    {
        Seed(p256dh: null);
        await _context.SaveChangesAsync();

        var response = await GetPushTokensHandler.Handle(
            new GetPushTokensForUsersRequest { UserIds = ["user-1"] }, _context);

        Assert.That(response.Sendable(ContractKind.WebPush), Is.Empty);
        Assert.That(response.Tokens, Has.Count.EqualTo(1), "still returned, just not sendable");
    }

    // ══════════════════════════════════════════════════════════════════════════ Dead-subscription
    // cleanup ══════════════════════════════════════════════════════════════════════════

    /// <summary>A dead subscription is not self-healing: the browser that made it is gone, so nothing
    /// will ever re-register or delete it from the client side.</summary>
    [Test]
    public async Task An_expired_endpoint_is_deleted()
    {
        Seed();
        await _context.SaveChangesAsync();

        await PushEndpointExpiredHandler.Handle(
            new PushEndpointExpiredEvent { Kind = ContractKind.WebPush, Token = Endpoint },
            _context, NullLogger<PushEndpointExpiredHandler>.Instance);
        await _context.SaveChangesAsync();

        Assert.That(await _context.UserPushTokens.CountAsync(), Is.Zero);
    }

    /// <summary>Keyed on <c>(kind, token)</c>, so a 410 from a browser's push service cannot take an FCM
    /// token that happens to hold the same string with it.</summary>
    [Test]
    public async Task Deleting_an_expired_endpoint_leaves_other_kinds_alone()
    {
        Seed();
        Seed(token: Endpoint, kind: PushTokenKind.Fcm, p256dh: null, auth: null);
        await _context.SaveChangesAsync();

        await PushEndpointExpiredHandler.Handle(
            new PushEndpointExpiredEvent { Kind = ContractKind.WebPush, Token = Endpoint },
            _context, NullLogger<PushEndpointExpiredHandler>.Instance);
        await _context.SaveChangesAsync();

        Assert.That((await _context.UserPushTokens.SingleAsync()).Kind, Is.EqualTo(PushTokenKind.Fcm));
    }

    /// <summary>Idempotent.</summary>
    [Test]
    public void Deleting_an_endpoint_that_is_already_gone_is_not_an_error()
    {
        Assert.DoesNotThrowAsync(() => PushEndpointExpiredHandler.Handle(
            new PushEndpointExpiredEvent { Kind = ContractKind.WebPush, Token = "never-existed" },
            _context, NullLogger<PushEndpointExpiredHandler>.Instance));
    }

    /// <summary>
    /// A recycled endpoint reassigned to a different account is still removed: what expired is the
    /// subscription, not the person.
    /// </summary>
    [Test]
    public async Task An_expired_endpoint_is_deleted_whoever_owns_it_now()
    {
        Seed(userId: "someone-else");
        await _context.SaveChangesAsync();

        await PushEndpointExpiredHandler.Handle(
            new PushEndpointExpiredEvent { Kind = ContractKind.WebPush, Token = Endpoint },
            _context, NullLogger<PushEndpointExpiredHandler>.Instance);
        await _context.SaveChangesAsync();

        Assert.That(await _context.UserPushTokens.CountAsync(), Is.Zero);
    }

    /// <summary>A genuine P-256 point and a 16-byte secret, so the validator is exercised against the
    /// shape a browser actually produces rather than against a placeholder.</summary>
    private static (string P256dh, string Auth) RealSubscription()
    {
        using var key = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y.Length));

        return (WebPushSubscription.Encode(point),
            WebPushSubscription.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
    }
}
