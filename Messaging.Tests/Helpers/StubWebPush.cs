using System.Net;
using AppEnvironment;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Identity.Contracts.Push;
using Messaging.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Messaging.Tests.Helpers;

/// <summary>
/// A real <see cref="WebPushSender"/> over a canned HTTP handler, plus the VAPID keypair and the
/// subscription rows a test needs to drive it.
/// </summary>
internal sealed class StubWebPush
{
    /// <summary>A syntactically real subscription: an FCM-hosted endpoint, a genuine P-256 point and a
    /// 16-byte auth secret. Generated once so a test can assert on ciphertext length without the
    /// numbers moving.</summary>
    public const string Endpoint = "https://fcm.googleapis.com/fcm/send/stub-endpoint-1";

    public string P256dh { get; }

    public string Auth { get; }

    public WebPushSender Sender { get; }

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Bodies as they went out, captured before the request was disposed.</summary>
    public List<byte[]> Bodies { get; } = [];

    private StubWebPush(HttpStatusCode status, string p256dh, string auth, IMessageBus bus)
    {
        P256dh = p256dh;
        Auth = auth;

        var handler = new StubHandler(status, Requests, Bodies);
        Sender = new WebPushSender(
            new StubHttpClientFactory(new HttpClient(handler)), bus, NullLogger<WebPushSender>.Instance);
    }

    /// <summary>Hands out the one canned client.</summary>
    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// A sender whose push service always answers <paramref name="status"/>, with a VAPID keypair
    /// installed on <c>Env.Vapid</c>.
    /// </summary>
    public static StubWebPush Create(IMessageBus bus, HttpStatusCode status = HttpStatusCode.Created)
    {
        using var ua = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = ua.ExportParameters(false).Q;
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y.Length));

        var auth = WebPushSubscription.Encode(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        ConfigureEnv();

        return new StubWebPush(status, WebPushSubscription.Encode(point), auth, bus);
    }

    /// <summary>A subscription row shaped the way <c>GetPushTokensHandler</c> shapes one.</summary>
    public PushTokenResponse Subscription(string userId = "user-1", string? endpoint = null) => new()
    {
        UserId = userId,
        Token = endpoint ?? Endpoint,
        Kind = PushTokenKind.WebPush,
        P256dh = P256dh,
        Auth = Auth,
    };

    /// <summary>Installs a freshly generated VAPID keypair on <c>Env.Vapid</c>.</summary>
    public static void ConfigureEnv()
    {
        using var vapid = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = vapid.ExportParameters(true);

        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X.Length));
        p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y.Length));

        var d = new byte[32];
        p.D!.CopyTo(d, 32 - p.D.Length);

        Env.Vapid.PublicKey = WebPushSubscription.Encode(point);
        Env.Vapid.PrivateKey = WebPushSubscription.Encode(d);
        Env.Vapid.Subject = "mailto:ops@venta.test";
    }

    /// <summary>Clears the keypair again. Call from TearDown.</summary>
    public static void ResetEnv()
    {
        Env.Vapid.PublicKey = string.Empty;
        Env.Vapid.PrivateKey = string.Empty;
    }

    private sealed class StubHandler(
        HttpStatusCode status,
        List<HttpRequestMessage> requests,
        List<byte[]> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read before returning: the sender disposes the request, and a body read afterwards is an
            // ObjectDisposedException inside a test rather than an assertion.
            bodies.Add(request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            requests.Add(request);

            return new HttpResponseMessage(status);
        }
    }
}
