using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Alba;
using AppEnvironment;
using Identity.Application.Dtos.Request;
using Identity.Contracts.Push;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PushTokenKind = Identity.Domain.Enums.PushTokenKind;

namespace Identity.Tests.Controllers;

/// <summary>
/// The HTTP surface a browser talks to: <c>GET push/vapid-public-key</c> to learn the
/// <c>applicationServerKey</c>, <c>POST self/push-token</c> to register a subscription, and
/// <c>DELETE self/push-token?endpoint=</c> to drop it at sign-out.
/// </summary>
[TestFixture]
public class WebPushEndpointTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private string _originalPublicKey = null!;
    private string _originalPrivateKey = null!;

    [SetUp]
    public void CaptureVapid()
    {
        _originalPublicKey = Env.Vapid.PublicKey;
        _originalPrivateKey = Env.Vapid.PrivateKey;
    }

    [TearDown]
    public void RestoreVapid()
    {
        Env.Vapid.PublicKey = _originalPublicKey;
        Env.Vapid.PrivateKey = _originalPrivateKey;
    }

    private static async Task<string> RegisterAndLoginAsync(string username)
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return (await tokenResult.ReadAsJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    /// <summary>A genuine P-256 point and a 16-byte secret - the shape a browser hands the client.</summary>
    private static (string P256dh, string Auth) Subscription()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y.Length));

        return (WebPushSubscription.Encode(point),
            WebPushSubscription.Encode(RandomNumberGenerator.GetBytes(16)));
    }

    private static string NewEndpoint() =>
        $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

    // ══════════════════════════════════════════════════════════════════════════ The VAPID public
    // key ══════════════════════════════════════════════════════════════════════════

    /// <summary>404, not an empty string.</summary>
    [Test]
    public async Task The_vapid_key_is_404_when_the_instance_has_no_keypair()
    {
        Env.Vapid.PublicKey = string.Empty;
        Env.Vapid.PrivateKey = string.Empty;

        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/users/push/vapid-public-key");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    /// <summary>
    /// Anonymous by design: it is a public key, and the client needs it before the notification
    /// permission prompt has been answered.
    /// </summary>
    [Test]
    public async Task The_vapid_key_is_served_anonymously_when_configured()
    {
        Env.Vapid.PublicKey = "test-public-key";
        Env.Vapid.PrivateKey = "test-private-key";

        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/users/push/vapid-public-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("publicKey").GetString(), Is.EqualTo("test-public-key"));
    }

    /// <summary>Both halves or neither.</summary>
    [Test]
    public async Task A_public_key_with_no_private_half_is_not_advertised()
    {
        Env.Vapid.PublicKey = "test-public-key";
        Env.Vapid.PrivateKey = string.Empty;

        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/users/push/vapid-public-key");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Registering a
    // subscription ══════════════════════════════════════════════════════════════════════════

    /// <summary>The endpoint lands in <c>token</c> and both keys are stored.</summary>
    [Test]
    public async Task A_browser_subscription_is_stored_with_its_endpoint_as_the_token()
    {
        var token = await RegisterAndLoginAsync($"wp{Guid.NewGuid():N}"[..15]);
        var (p256dh, auth) = Subscription();
        var endpoint = NewEndpoint();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreatePushTokenDto
            {
                Kind = PushTokenKind.WebPush,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var stored = await ctx.UserPushTokens.SingleAsync(t => t.Token == endpoint);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Kind, Is.EqualTo(PushTokenKind.WebPush));
            Assert.That(stored.P256dh, Is.EqualTo(p256dh));
            Assert.That(stored.Auth, Is.EqualTo(auth));
            Assert.That(stored.IsComplete, Is.True);
        });
    }

    /// <summary>
    /// No <c>token</c> field is sent - a browser has no token - so the request must not be refused
    /// for lacking one.
    /// </summary>
    [Test]
    public async Task A_subscription_needs_no_token_field()
    {
        var token = await RegisterAndLoginAsync($"wpnt{Guid.NewGuid():N}"[..15]);
        var (p256dh, auth) = Subscription();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreatePushTokenDto
            {
                Kind = PushTokenKind.WebPush,
                Endpoint = NewEndpoint(),
                P256dh = p256dh,
                Auth = auth,
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });
    }

    /// <summary>Refused at registration, in front of the only party that can fix it.</summary>
    [TestCase("not-a-p256-point", null, TestName = "a p256dh that is not a point")]
    [TestCase(null, "short", TestName = "an auth secret of the wrong length")]
    public async Task A_subscription_that_cannot_be_encrypted_to_is_refused(string? badP256dh, string? badAuth)
    {
        var userToken = await RegisterAndLoginAsync($"wpbad{Guid.NewGuid():N}"[..15]);
        var (p256dh, auth) = Subscription();
        var endpoint = NewEndpoint();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Post.Json(new CreatePushTokenDto
            {
                Kind = PushTokenKind.WebPush,
                Endpoint = endpoint,
                P256dh = badP256dh ?? p256dh,
                Auth = badAuth ?? auth,
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        Assert.That(await ctx.UserPushTokens.AnyAsync(t => t.Token == endpoint), Is.False,
            "a refused subscription must leave no row behind");
    }

    /// <summary>The server POSTs to this value, so a plaintext one is a request to make an unencrypted
    /// outbound call to somewhere unverified.</summary>
    [Test]
    public async Task A_plaintext_endpoint_is_refused()
    {
        var userToken = await RegisterAndLoginAsync($"wphttp{Guid.NewGuid():N}"[..15]);
        var (p256dh, auth) = Subscription();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Post.Json(new CreatePushTokenDto
            {
                Kind = PushTokenKind.WebPush,
                Endpoint = "http://fcm.googleapis.com/fcm/send/abc",
                P256dh = p256dh,
                Auth = auth,
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    /// <summary>
    /// A browser rotates its keys whenever it re-subscribes, and it can re-subscribe against the
    /// same endpoint.
    /// </summary>
    [Test]
    public async Task Re_subscribing_on_the_same_endpoint_refreshes_the_keys()
    {
        var userToken = await RegisterAndLoginAsync($"wpre{Guid.NewGuid():N}"[..15]);
        var endpoint = NewEndpoint();
        var (firstP256dh, firstAuth) = Subscription();
        var (secondP256dh, secondAuth) = Subscription();

        async Task SubscribeAsync(string p256dh, string auth, HttpStatusCode expected)
        {
            await Host.Scenario(x =>
            {
                x.WithBearerToken(userToken);
                x.Post.Json(new CreatePushTokenDto
                {
                    Kind = PushTokenKind.WebPush,
                    Endpoint = endpoint,
                    P256dh = p256dh,
                    Auth = auth,
                }).ToUrl("/api/v1/users/self/push-token");
                x.StatusCodeShouldBe(expected);
            });
        }

        await SubscribeAsync(firstP256dh, firstAuth, HttpStatusCode.Created);
        await SubscribeAsync(secondP256dh, secondAuth, HttpStatusCode.Accepted);

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var rows = await ctx.UserPushTokens.Where(t => t.Token == endpoint).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "one row per subscription, enforced by (kind, token)");
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].P256dh, Is.EqualTo(secondP256dh));
            Assert.That(rows[0].Auth, Is.EqualTo(secondAuth));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Dropping it at
    // sign-out ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>?endpoint=</c> is an alias for <c>?token=</c>, because that is the name a browser knows
    /// the value by: a client holding a <c>PushSubscription</c> has no "token" to send.
    /// </summary>
    [Test]
    public async Task A_subscription_can_be_deleted_by_endpoint()
    {
        var userToken = await RegisterAndLoginAsync($"wpdel{Guid.NewGuid():N}"[..15]);
        var (p256dh, auth) = Subscription();
        var endpoint = NewEndpoint();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Post.Json(new CreatePushTokenDto
            {
                Kind = PushTokenKind.WebPush,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Delete.Url($"/api/v1/users/self/push-token?endpoint={Uri.EscapeDataString(endpoint)}");
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        Assert.That(await ctx.UserPushTokens.AnyAsync(t => t.Token == endpoint), Is.False);
    }

    /// <summary>Neither spelling supplied is a 400, not a 500 or a delete of everything.</summary>
    [Test]
    public async Task Deleting_with_neither_token_nor_endpoint_is_a_bad_request()
    {
        var userToken = await RegisterAndLoginAsync($"wpnone{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Delete.Url("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }
}
