using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace Identity.Tests.Controllers;

/// <summary>
/// Pins the one OpenIddict behaviour docs/specs/sso.md is built on: the configured issuer and the
/// hostname a request arrives on are independent.
/// </summary>
[TestFixture]
public class IssuerHostBindingTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    /// <summary>The issuer the server is actually running with, as OpenIddict resolved it.</summary>
    private static Uri ConfiguredIssuer =>
        Host.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue.Issuer
        ?? throw new InvalidOperationException(
            "No issuer is configured. These tests exist to prove the issuer is independent of the "
            + "request host; with no issuer set OpenIddict infers one per request and they prove nothing.");

    private static async Task<string> RegisterAsync()
    {
        var username = $"iss{Guid.NewGuid():N}"[..15];

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

        return username;
    }

    /// <summary>
    /// Reads the <c>iss</c> claim without pulling in a JWT library: split on the dots, base64url-decode
    /// the payload. Access token encryption is disabled (Program.cs:131), so the payload is readable.
    /// </summary>
    private static string ReadIssuer(string accessToken)
    {
        var segments = accessToken.Split('.');
        Assert.That(segments, Has.Length.GreaterThanOrEqualTo(2),
            "the access token is not a JWT - if encryption was re-enabled this test needs to decrypt it");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return document.RootElement.GetProperty("iss").GetString()!;
    }

    private static async Task<string> TokenFromHostAsync(string username, string? host)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");

            if (host is not null)
            {
                x.WithRequestHeader("Host", host);
            }

            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var document = JsonDocument.Parse(result.ReadAsText());
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>The premise.</summary>
    [Test]
    public void The_test_host_does_not_serve_on_the_configured_issuer_host()
    {
        Assert.That(ConfiguredIssuer.Host, Is.Not.EqualTo("localhost"),
            "these tests compare a request host against the issuer host; if they are the same the "
            + "comparison is vacuous");
    }

    /// <summary>
    /// Normal case: a request arriving on a host that is not the issuer is served, and the token it
    /// mints carries the configured issuer rather than the host it was asked on.
    /// </summary>
    [Test]
    public async Task Token_endpoint_serves_a_request_whose_host_is_not_the_issuer()
    {
        var username = await RegisterAsync();

        var token = await TokenFromHostAsync(username, host: null);

        Assert.That(ReadIssuer(token), Is.EqualTo(ConfiguredIssuer.AbsoluteUri),
            "the iss claim must follow SetIssuer, not the request");
    }

    /// <summary>
    /// Edge case, and the one the migration actually depends on: an explicit, unrelated public
    /// hostname.
    /// </summary>
    [TestCase("api.venta.gg")]
    [TestCase("auth.venta.gg")]
    [TestCase("some-instance.example.org")]
    public async Task Token_endpoint_serves_any_host_and_always_stamps_the_configured_issuer(string host)
    {
        var username = await RegisterAsync();

        var token = await TokenFromHostAsync(username, host);

        Assert.That(ReadIssuer(token), Is.EqualTo(ConfiguredIssuer.AbsoluteUri),
            $"a request on {host} must still mint tokens issued by {ConfiguredIssuer}");
    }

    /// <summary>
    /// The iss claim is a normalised absolute URI and therefore ends in a slash, while
    /// <c>INSTANCE_URL</c> is written by an operator and almost never does.
    /// </summary>
    [Test]
    public async Task The_iss_claim_is_a_normalised_absolute_uri_and_keeps_its_trailing_slash()
    {
        var username = await RegisterAsync();

        var issuer = ReadIssuer(await TokenFromHostAsync(username, host: null));

        Assert.Multiple(() =>
        {
            Assert.That(issuer, Does.EndWith("/"),
                "OpenIddict stamps Uri.AbsoluteUri, which always has a path of at least '/'");
            Assert.That(issuer, Is.Not.EqualTo(AppEnvironment.Env.GeneralConfiguration.InstanceBaseUrl),
                "InstanceBaseUrl deliberately trims the trailing slash, so it is NOT a usable ValidIssuer");
        });
    }
}
