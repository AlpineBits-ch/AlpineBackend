using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using AppEnvironment;
using Identity.Application.Dtos.Request;
using Identity.Application.Services.Sso;

namespace Identity.Tests.Controllers;

/// <summary>
/// The published contract of the identity provider: what a partner site reads out of
/// <c>/.well-known/openid-configuration</c> before it can federate at all, plus the claim-destination
/// policy that decides what it is allowed to see afterwards. See docs/specs/sso.md.
/// </summary>
[TestFixture]
public class OidcDiscoveryTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<JsonElement> DiscoveryAsync()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/.well-known/openid-configuration");
            x.StatusCodeShouldBeOk();
        });

        return JsonDocument.Parse(result.ReadAsText()).RootElement.Clone();
    }

    private static string[] Strings(JsonElement document, string property) =>
        document.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    // ── Discovery ───────────────────────────────────────────────────────────

    /// <summary>The issuer is the auth host, not the API host.</summary>
    [Test]
    public async Task Discovery_advertises_the_configured_auth_issuer()
    {
        var document = await DiscoveryAsync();

        Assert.That(document.GetProperty("issuer").GetString()!.TrimEnd('/'),
            Is.EqualTo(Env.AuthConfiguration.IssuerUrl.TrimEnd('/')));
    }

    /// <summary>A relying party discovers every endpoint it will use.</summary>
    [TestCase("authorization_endpoint", "/connect/authorize")]
    [TestCase("token_endpoint", "/connect/token")]
    [TestCase("userinfo_endpoint", "/connect/userinfo")]
    [TestCase("end_session_endpoint", "/connect/logout")]
    [TestCase("revocation_endpoint", "/connect/revoke")]
    [TestCase("jwks_uri", "/.well-known/jwks")]
    public async Task Discovery_advertises_endpoint(string property, string path)
    {
        var document = await DiscoveryAsync();

        Assert.That(document.GetProperty(property).GetString(), Does.EndWith(path));
    }

    /// <summary>
    /// Authorization code is offered, and the grants that predate it survive the change - the mobile
    /// app still signs in with a password, and the QR and Steam flows are still redeemable.
    /// </summary>
    [Test]
    public async Task Discovery_offers_authorization_code_without_dropping_the_existing_grants()
    {
        var grants = Strings(await DiscoveryAsync(), "grant_types_supported");

        Assert.That(grants, Is.SupersetOf(new[]
        {
            "authorization_code",
            "password",
            "refresh_token",
            "client_credentials",
            "urn:echo:params:oauth:grant-type:steam",
            "urn:echo:params:oauth:grant-type:qr_login",
        }));
    }

    /// <summary>PKCE is required of every client, so S256 has to be advertised.</summary>
    [Test]
    public async Task Discovery_advertises_S256_proof_key()
    {
        var methods = Strings(await DiscoveryAsync(), "code_challenge_methods_supported");

        Assert.That(methods, Does.Contain("S256"));
    }

    /// <summary>Negative: no implicit or hybrid flow.</summary>
    [Test]
    public async Task Discovery_does_not_offer_implicit_or_hybrid_flows()
    {
        var responseTypes = Strings(await DiscoveryAsync(), "response_types_supported");

        Assert.That(responseTypes, Has.None.Contains("token"),
            "an access token must never be returned from the authorization endpoint");
    }

    // ── Claim destinations ──────────────────────────────────────────────────

    /// <summary>The security stamp must not reach any token.</summary>
    [Test]
    public async Task The_security_stamp_never_reaches_the_access_token()
    {
        var username = $"dst{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var result = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBeOk();
        });

        var accessToken = JsonDocument.Parse(result.ReadAsText())
            .RootElement.GetProperty("access_token").GetString()!;

        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = Encoding.UTF8.GetString(Convert.FromBase64String(payload));

        Assert.Multiple(() =>
        {
            Assert.That(claims, Does.Not.Contain("SecurityStamp"),
                "the security stamp is the value that revokes derived credentials");

            // The claims eight services actually read must survive the tightening.
            Assert.That(claims, Does.Contain(VentaClaimDestinations.SessionId),
                "refresh enforces revocation through this claim");
            Assert.That(claims, Does.Contain("sub"));
        });
    }
}
