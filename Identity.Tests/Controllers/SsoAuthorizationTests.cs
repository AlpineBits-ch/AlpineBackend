using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services.Steam;

namespace Identity.Tests.Controllers;

/// <summary>
/// The browser half of the identity provider: the authorization endpoint, the SSO cookie, the
/// parked request and the client registry behind them.
/// </summary>
[TestFixture]
public class SsoAuthorizationTests
{
    private const string Password = "SecurePass123!";
    private const string FirstParty = "test-first-party";
    private const string ThirdParty = "test-third-party";
    private const string FirstPartyRedirect = "https://rp.example.com/callback";
    private const string Loopback = "test-loopback";
    private const string LoopbackRedirect = "http://localhost:4200/auth/callback";

    private static IAlbaHost Host => AppFixture.Host;

    // ── Fixtures ────────────────────────────────────────────────────────────

    private sealed record Account(string Username, string Email);

    private static async Task<Account> RegisterAsync()
    {
        var username = $"sso{Guid.NewGuid():N}"[..15];
        var email = $"{username}@example.com";

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-25),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        return new Account(username, email);
    }

    private static async Task<string> AccessTokenAsync(string usernameOrEmail)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = usernameOrEmail,
                ["password"] = Password,
                ["client_id"] = "echo",
                ["scope"] = "openid profile email",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBeOk();
        });

        return JsonDocument.Parse(result.ReadAsText()).RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Signs a browser in and returns the raw cookie header to replay on later requests.</summary>
    private static async Task<string> SsoCookieAsync(string usernameOrEmail)
    {
        var token = await AccessTokenAsync(usernameOrEmail);

        var result = await Host.Scenario(x =>
        {
            x.Post.Url("/api/v1/sso/session");
            x.WithRequestHeader("Authorization", $"Bearer {token}");
            x.StatusCodeShouldBeOk();
        });

        var setCookie = result.Context.Response.Headers.SetCookie.ToString();

        Assert.That(setCookie, Does.Contain("__Host-venta_sso"),
            "establishing the SSO session must set the browser cookie");
        Assert.That(setCookie, Does.Contain("secure").IgnoreCase,
            "the __Host- prefix is only honoured on a Secure cookie");

        return setCookie.Split(';')[0];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Pkce(string Verifier, string Challenge);

    private static Pkce NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new Pkce(verifier, challenge);
    }

    private static string AuthorizeUrl(string clientId, string redirectUri, string challenge,
        string scope = "openid profile email", string? prompt = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = "st-" + Guid.NewGuid().ToString("N")[..8],
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = prompt,
        };

        var parts = query.Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");

        return "/connect/authorize?" + string.Join('&', parts);
    }

    private static async Task<string?> LocationAsync(string url, string? cookie = null)
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url(url);
            if (cookie is not null) x.WithRequestHeader("Cookie", cookie);
            x.StatusCodeShouldBe(HttpStatusCode.Found);
        });

        return result.Context.Response.Headers.Location.ToString();
    }

    // ── The round trip ──────────────────────────────────────────────────────

    /// <summary>
    /// Normal: a signed-in browser completes an authorization and the code exchanges for tokens.
    /// </summary>
    [Test]
    public async Task A_signed_in_browser_completes_the_authorization_code_flow()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);
        var pkce = NewPkce();

        var location = await LocationAsync(AuthorizeUrl(FirstParty, FirstPartyRedirect, pkce.Challenge), cookie);

        Assert.That(location, Does.StartWith(FirstPartyRedirect),
            "a first-party client skips consent, so authorize completes on the first pass");

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location!).Query)["code"];
        Assert.That(code, Is.Not.Null.And.Not.Empty);

        var exchange = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstParty,
                ["redirect_uri"] = FirstPartyRedirect,
                ["code"] = code!,
                ["code_verifier"] = pkce.Verifier,
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBeOk();
        });

        var tokens = JsonDocument.Parse(exchange.ReadAsText()).RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(tokens.TryGetProperty("access_token", out _), Is.True);
            Assert.That(tokens.TryGetProperty("id_token", out _), Is.True,
                "the openid scope was granted, so an identity token is part of the response");
        });
    }

    /// <summary>
    /// Edge: a plain-http loopback redirect URI, which is what `ng serve` on localhost:4200 uses.
    /// </summary>
    [Test]
    public async Task A_loopback_http_redirect_uri_completes_the_flow()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);
        var pkce = NewPkce();

        var location = await LocationAsync(AuthorizeUrl(Loopback, LoopbackRedirect, pkce.Challenge), cookie);

        Assert.That(location, Does.StartWith(LoopbackRedirect),
            "http on a loopback host must be accepted, or local development cannot sign in");

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location!).Query)["code"];

        var exchange = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = Loopback,
                ["redirect_uri"] = LoopbackRedirect,
                ["code"] = code!,
                ["code_verifier"] = pkce.Verifier,
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBeOk();
        });

        Assert.That(JsonDocument.Parse(exchange.ReadAsText()).RootElement.TryGetProperty("access_token", out _),
            Is.True);
    }

    /// <summary>Edge: no browser session.</summary>
    [Test]
    public async Task An_unauthenticated_authorization_parks_the_request_and_redirects_to_the_sign_in_page()
    {
        var location = await LocationAsync(AuthorizeUrl(FirstParty, FirstPartyRedirect, NewPkce().Challenge));

        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith("/login?rq="));
            Assert.That(location, Does.Not.Contain("redirect_uri"));
            Assert.That(location, Does.Not.Contain("code_challenge"));
        });
    }

    /// <summary>
    /// The parked request is readable by the sign-in page - that is what lets it say "continue to
    /// Test Site" rather than presenting a naked form - and exposes nothing that alters the request.
    /// </summary>
    [Test]
    public async Task The_parked_request_projects_the_client_and_its_scopes()
    {
        var location = await LocationAsync(AuthorizeUrl(ThirdParty, "https://third.example.com/callback",
            NewPkce().Challenge, scope: "openid profile"));

        var rq = location!["/login?rq=".Length..];

        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/sso/request/{rq}");
            x.StatusCodeShouldBeOk();
        });

        var projection = JsonDocument.Parse(result.ReadAsText()).RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(projection.GetProperty("clientName").GetString(), Is.EqualTo("Third Party"));
            Assert.That(projection.GetProperty("kind").GetString(), Is.EqualTo("authorize"));
            Assert.That(projection.GetProperty("resumeUrl").GetString(),
                Does.StartWith("/connect/authorize/resume?rq="));

            // openid is filtered out of the display list: "confirm who you are" is the premise of
            // the screen rather than a thing being granted.
            var scopes = projection.GetProperty("scopes").EnumerateArray()
                .Select(scope => scope.GetProperty("name").GetString()).ToArray();

            Assert.That(scopes, Is.EquivalentTo(new[] { "profile" }));
        });
    }

    /// <summary>
    /// Negative: a redirect URI the client never registered is refused, and refused without a
    /// redirect.
    /// </summary>
    [Test]
    public async Task An_unregistered_redirect_uri_is_refused_without_redirecting()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url(AuthorizeUrl(FirstParty, "https://attacker.example.com/callback", NewPkce().Challenge));
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    /// <summary>Negative: PKCE is enforced at the exchange, not merely advertised.</summary>
    [Test]
    public async Task A_code_cannot_be_exchanged_with_the_wrong_verifier()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);

        var location = await LocationAsync(
            AuthorizeUrl(FirstParty, FirstPartyRedirect, NewPkce().Challenge), cookie);

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location!).Query)["code"];

        await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstParty,
                ["redirect_uri"] = FirstPartyRedirect,
                ["code"] = code!,
                ["code_verifier"] = Base64Url(RandomNumberGenerator.GetBytes(32)),
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    /// <summary>
    /// Edge: <c>prompt=login</c> is honoured even though the browser has a live session - and, on
    /// the way back, does not loop.
    /// </summary>
    [Test]
    public async Task Prompt_login_re_prompts_once_and_then_completes()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);

        var first = await LocationAsync(
            AuthorizeUrl(FirstParty, FirstPartyRedirect, NewPkce().Challenge, prompt: "login"), cookie);

        Assert.That(first, Does.StartWith("/login?rq="), "a live session is not enough for prompt=login");

        var rq = first!["/login?rq=".Length..];

        // What the sign-in page does once it has re-authenticated: follow the resume URL, which
        // reconstitutes the original request and carries the parked id with it.
        var resumed = await LocationAsync($"/connect/authorize/resume?rq={rq}", cookie);
        var completed = await LocationAsync(resumed!, cookie);

        Assert.That(completed, Does.StartWith(FirstPartyRedirect),
            "the second pass must complete rather than re-prompt");
    }

    /// <summary>
    /// Negative: a third-party client does not get to skip the consent screen, however first-party
    /// the surrounding infrastructure is.
    /// </summary>
    [Test]
    public async Task A_third_party_client_is_sent_to_the_consent_screen()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);

        var location = await LocationAsync(
            AuthorizeUrl(ThirdParty, "https://third.example.com/callback", NewPkce().Challenge,
                scope: "openid profile"),
            cookie);

        Assert.That(location, Does.StartWith("/consent?rq="));
    }

    /// <summary>
    /// Negative: <c>prompt=none</c> with no session answers <c>login_required</c> at the client's
    /// own redirect URI rather than rendering anything.
    /// </summary>
    [Test]
    public async Task Prompt_none_without_a_session_answers_login_required()
    {
        var location = await LocationAsync(
            AuthorizeUrl(FirstParty, FirstPartyRedirect, NewPkce().Challenge, prompt: "none"));

        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith(FirstPartyRedirect));
            Assert.That(location, Does.Contain("error=login_required"));
        });
    }

    /// <summary>
    /// The cookie is an assertion about the past, not a standing permission: revoking the browser's
    /// session takes effect at the next authorization rather than whenever the cookie expires.
    /// </summary>
    [Test]
    public async Task Revoking_the_browser_session_stops_further_authorizations()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);

        await Host.Scenario(x =>
        {
            x.Delete.Url("/api/v1/sso/session");
            x.WithRequestHeader("Cookie", cookie);
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        var location = await LocationAsync(
            AuthorizeUrl(FirstParty, FirstPartyRedirect, NewPkce().Challenge), cookie);

        Assert.That(location, Does.StartWith("/login?rq="),
            "the cookie is still in the browser, but the session behind it is gone");
    }

    /// <summary>Sign-out is never a bare GET side effect.</summary>
    [Test]
    public async Task Logout_without_an_id_token_hint_asks_for_confirmation()
    {
        var account = await RegisterAsync();
        var cookie = await SsoCookieAsync(account.Username);

        var location = await LocationAsync(
            $"/connect/logout?client_id={FirstParty}&post_logout_redirect_uri="
            + Uri.EscapeDataString("https://rp.example.com/"), cookie);

        Assert.That(location, Does.StartWith("/logout?rq="));
    }

    // ── The password grant ──────────────────────────────────────────────────

    /// <summary>Normal: the sign-in field says "Username or email", and now both work.</summary>
    [Test]
    public async Task The_password_grant_accepts_an_email_address()
    {
        var account = await RegisterAsync();

        var token = await AccessTokenAsync(account.Email);

        Assert.That(token, Is.Not.Empty);
    }

    /// <summary>
    /// Negative: an unknown identifier is refused exactly as a wrong password is - same status,
    /// same empty body - whether it looks like an address or not.
    /// </summary>
    [TestCase("nobody@example.com")]
    [TestCase("definitely-not-a-user")]
    public async Task An_unknown_identifier_is_refused_like_a_wrong_password(string identifier)
    {
        await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = identifier,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── Steam return targets ────────────────────────────────────────────────

    /// <summary>
    /// Negative, and the one worth being blunt about: the callback URL carries a single-use login
    /// ticket in its query string, so honouring a caller-named return target would hand the ticket
    /// to whoever named it.
    /// </summary>
    [TestCase("https://attacker.example.com/steal")]
    [TestCase("venta://steam-auth.attacker.example.com")]
    [TestCase("")]
    [TestCase(null)]
    public void An_unlisted_steam_return_target_falls_back_to_the_default(string? requested)
    {
        Assert.That(SteamReturnTargets.Resolve(requested), Is.EqualTo(SteamReturnTargets.Default));
    }

    [Test]
    public void The_auth_site_is_an_allowed_steam_return_target()
    {
        Assert.That(SteamReturnTargets.Resolve(SteamReturnTargets.AuthSite),
            Is.EqualTo(SteamReturnTargets.AuthSite));
    }
}
