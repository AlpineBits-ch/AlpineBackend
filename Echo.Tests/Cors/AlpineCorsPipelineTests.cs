using System.Net;
using AppEnvironment;
using Echo.Cors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Echo.Tests.Cors;

/// <summary>
/// The CORS policy driven through a real ASP.NET Core pipeline, the same way
/// <c>GatewayRateLimitHarness</c> drives the real limiter and for the same reason: asserting that
/// <c>AddAlpineCors</c> was called, or reading the policy object back out of the options, would
/// pass against a policy the browser rejects.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AlpineCorsPipelineTests
{
    private const string WebOrigin = "https://app.venta.gg";
    private const string DesktopOrigin = "tauri://localhost";
    private const string HostileOrigin = "https://venta.gg.attacker.example";

    /// <summary>A proxied API path, as ProxyConfig shapes them.</summary>
    private const string ApiPath = "/api/v1/guild/channels";

    /// <summary>The realtime hub's negotiate.</summary>
    private const string NegotiatePath = "/api/v1/ws/hub/negotiate";

    private WebApplication _app = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddAlpineCors();

        _app = builder.Build();
        _app.UseRouting();
        _app.UseCors(AlpineCors.PolicyName);

        // Stands in for the gateway's own surfaces.
        foreach (var path in new[] { ApiPath, NegotiatePath })
        {
            _app.MapMethods(path, ["GET", "POST", "PUT", "PATCH", "DELETE"], () => "ok");
        }

        await _app.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private Task<HttpContext> PreflightAsync(string origin, string method, string path = ApiPath, string? headers = null) =>
        _app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Options;
            context.Request.Path = path;
            context.Request.Headers.Origin = origin;
            context.Request.Headers.AccessControlRequestMethod = method;
            if (headers is not null) context.Request.Headers.AccessControlRequestHeaders = headers;
        });

    private Task<HttpContext> RequestAsync(string origin, string method = "GET", string path = ApiPath) =>
        _app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Headers.Origin = origin;
        });

    /// <summary>The whole feature in one assertion.</summary>
    [Test]
    public async Task The_web_origin_passes_preflight()
    {
        var context = await PreflightAsync(WebOrigin, "GET", headers: "authorization,x-device-id");

        Assert.That(context.Response.StatusCode, Is.EqualTo((int)HttpStatusCode.NoContent));
        Assert.That(context.Response.Headers.AccessControlAllowOrigin.ToString(), Is.EqualTo(WebOrigin));
    }

    /// <summary>
    /// PUT, PATCH and DELETE are all used by the client (payment handles, absences, admin
    /// federation), and each one preflights on its own method.
    /// </summary>
    [TestCase("GET")]
    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task Every_method_the_client_uses_passes_preflight(string method)
    {
        var context = await PreflightAsync(WebOrigin, method);

        Assert.That(context.Response.Headers.AccessControlAllowOrigin.ToString(), Is.EqualTo(WebOrigin));
        Assert.That(context.Response.Headers.AccessControlAllowMethods.ToString(), Does.Contain(method));
    }

    /// <summary>The realtime hub.</summary>
    [Test]
    public async Task The_realtime_negotiate_is_credentialed_and_origin_exact()
    {
        var context = await PreflightAsync(WebOrigin, "POST", NegotiatePath);

        Assert.That(context.Response.Headers.AccessControlAllowOrigin.ToString(), Is.EqualTo(WebOrigin));
        Assert.That(context.Response.Headers.AccessControlAllowCredentials.ToString(), Is.EqualTo("true"));
    }

    /// <summary>The pair that must never appear together.</summary>
    [Test]
    public async Task Credentialed_responses_never_carry_a_wildcard_origin()
    {
        var context = await RequestAsync(WebOrigin);

        Assert.That(context.Response.Headers.AccessControlAllowOrigin.ToString(), Is.Not.EqualTo("*"));
        Assert.That(context.Response.Headers.AccessControlAllowCredentials.ToString(), Is.EqualTo("true"));
    }

    /// <summary>
    /// A credentialed policy must vary on Origin or a shared cache can serve one origin's
    /// <c>Access-Control-Allow-Origin</c> to another, which fails as a CORS error for whichever
    /// origin lost the race.
    /// </summary>
    [Test]
    public async Task An_allowed_response_varies_on_origin()
    {
        var context = await RequestAsync(WebOrigin);

        Assert.That(context.Response.Headers.Vary.ToString(), Does.Contain("Origin"));
    }

    /// <summary>A host that merely contains our domain is a different origin.</summary>
    [Test]
    public async Task An_unlisted_origin_gets_no_allow_origin_header()
    {
        var preflight = await PreflightAsync(HostileOrigin, "GET");
        var request = await RequestAsync(HostileOrigin);

        Assert.That(preflight.Response.Headers.AccessControlAllowOrigin.ToString(), Is.Empty);
        Assert.That(request.Response.Headers.AccessControlAllowOrigin.ToString(), Is.Empty);
    }

    /// <summary>The desktop app must keep working: it is the same policy, and Tauri's webview origin
    /// is not derivable from anything.</summary>
    [Test]
    public async Task The_desktop_webview_origin_still_passes()
    {
        var context = await PreflightAsync(DesktopOrigin, "POST");

        Assert.That(context.Response.Headers.AccessControlAllowOrigin.ToString(), Is.EqualTo(DesktopOrigin));
    }

    /// <summary>
    /// Named response headers, on the actual response rather than the preflight - this is the
    /// header browsers read <c>Access-Control-Expose-Headers</c> from.
    /// </summary>
    [TestCase("Date")]
    [TestCase("ETag")]
    [TestCase("Retry-After")]
    public async Task The_headers_the_client_reads_are_exposed(string header)
    {
        var context = await RequestAsync(WebOrigin);

        Assert.That(context.Response.Headers.AccessControlExposeHeaders.ToString(), Does.Contain(header));
    }

    /// <summary>
    /// Every request preflights here, so an uncached preflight doubles the request count of a cold
    /// start. Chrome caches for five seconds when the server says nothing.
    /// </summary>
    [Test]
    public async Task Preflights_are_cacheable()
    {
        var context = await PreflightAsync(WebOrigin, "GET");

        Assert.That(context.Response.Headers.AccessControlMaxAge.ToString(),
            Is.EqualTo(((int)AlpineCors.PreflightMaxAge.TotalSeconds).ToString()));
    }

    /// <summary>A browser will not honour a Max-Age above two hours; it clamps it.</summary>
    [Test]
    public void The_preflight_max_age_is_within_what_browsers_honour()
    {
        Assert.That(AlpineCors.PreflightMaxAge, Is.LessThanOrEqualTo(TimeSpan.FromHours(2)));
    }

    /// <summary>The policy's origins are the shared list, not a second copy that can drift from
    /// Federation's.</summary>
    [Test]
    public void The_policy_uses_the_shared_origin_list()
    {
        Assert.That(ClientOrigins.Allowed, Contains.Item(WebOrigin));
        Assert.That(ClientOrigins.Allowed, Has.No.Member(ClientOrigins.AnyOrigin));
    }
}
