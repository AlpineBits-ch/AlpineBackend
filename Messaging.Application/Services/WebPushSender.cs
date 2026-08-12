using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AppEnvironment;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Identity.Contracts.Push;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>What a push service said, reduced to what a caller can act on.</summary>
public enum WebPushOutcome
{
    /// <summary>Accepted for delivery (201, or any 2xx).</summary>
    Delivered,

    /// <summary>404 or 410: the subscription is gone and the row has been reported for deletion.</summary>
    Expired,

    /// <summary>413: the encrypted body exceeded what this push service accepts.</summary>
    TooLarge,

    /// <summary>429, or a 5xx, or a transport failure. The subscription is fine; this attempt is not.</summary>
    Throttled,

    /// <summary>Nothing was attempted - no VAPID keypair, or a row missing its keys.</summary>
    NotAttempted,
}

/// <summary>
/// Sends one Web Push message per subscription (RFC 8030), with the payload encrypted per RFC 8291
/// and the request authorised per RFC 8292.
/// </summary>
public class WebPushSender(IHttpClientFactory httpClientFactory, IMessageBus bus, ILogger<WebPushSender> logger)
{
    /// <summary>Name of the HTTP client this resolves, registered by
    /// <see cref="WebPushServiceCollectionExtensions.AddWebPush"/>.</summary>
    public const string HttpClientName = "WebPush";

    private readonly HttpClient http = httpClientFactory.CreateClient(HttpClientName);

    /// <summary>The <c>Urgency</c> header (RFC 8030 §5.3).</summary>
    private const string Urgency = "normal";

    /// <summary>Sends to one subscription.</summary>
    /// <param name="subscription">A <c>WebPush</c> row, with its keys.</param>
    /// <param name="payload">JSON the service worker will read.</param>
    /// <param name="topic">
    /// Optional RFC 8030 <c>Topic</c>: a later message with the same topic replaces an undelivered
    /// earlier one at the push service.
    /// </param>
    public async Task<WebPushOutcome> SendAsync(
        PushTokenResponse subscription,
        string payload,
        string? topic = null,
        CancellationToken ct = default)
    {
        if (subscription.Kind != PushTokenKind.WebPush)
        {
            throw new ArgumentException("Not a Web Push subscription.", nameof(subscription));
        }

        // Neither of these is an error worth a log line per notification: an instance with no keypair
        // has Web Push switched off by configuration, and a row without keys was already refused at
        // registration, so seeing one means a build older than that check wrote it.
        if (!Env.Vapid.IsConfigured) return WebPushOutcome.NotAttempted;
        if (!subscription.IsSendable) return WebPushOutcome.NotAttempted;

        if (!Uri.TryCreate(subscription.Token, UriKind.Absolute, out var endpoint))
        {
            logger.LogWarning("Web Push row {Kind} has an endpoint that is not a URL; skipping",
                subscription.Kind);
            return WebPushOutcome.NotAttempted;
        }

        byte[] body;
        string authorization;
        try
        {
            body = WebPushEncryption.Encrypt(
                Encoding.UTF8.GetBytes(payload),
                WebPushSubscription.Decode(subscription.P256dh!),
                WebPushSubscription.Decode(subscription.Auth!));

            authorization = WebPushEncryption.BuildAuthorization(
                endpoint,
                Env.Vapid.Subject,
                WebPushSubscription.Decode(Env.Vapid.PublicKey),
                WebPushSubscription.Decode(Env.Vapid.PrivateKey),
                DateTimeOffset.UtcNow.Add(Env.Vapid.TokenLifetime));
        }
        catch (ArgumentOutOfRangeException e)
        {
            // Distinguished from a 413 on purpose: this one we can see coming, and the fix is on our
            // side. Logged loudly because it means a payload builder has no ceiling.
            logger.LogError(e, "Web Push payload too large to encrypt");
            return WebPushOutcome.TooLarge;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not encrypt a Web Push message");
            return WebPushOutcome.NotAttempted;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add(WebPushEncryption.ContentEncoding);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        // Required by RFC 8030. Some push services answer 400 without it, which reads as a payload bug.
        request.Headers.TryAddWithoutValidation("TTL",
            ((int)Env.Vapid.MessageTtl.TotalSeconds).ToString());
        request.Headers.TryAddWithoutValidation("Urgency", Urgency);
        if (!string.IsNullOrWhiteSpace(topic))
        {
            // A Topic must be short base64url.
            request.Headers.TryAddWithoutValidation("Topic", TopicOf(topic));
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Web Push request to {Host} failed", endpoint.Host);
            return WebPushOutcome.Throttled;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return WebPushOutcome.Delivered;

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                case HttpStatusCode.Gone:
                    // The only two statuses that mean the subscription itself is finished.
                    await bus.PublishAsync(new PushEndpointExpiredEvent
                    {
                        Kind = PushTokenKind.WebPush,
                        Token = subscription.Token,
                    });
                    logger.LogInformation(
                        "Web Push endpoint on {Host} reported {Status}; reported for deletion",
                        endpoint.Host, (int)response.StatusCode);
                    return WebPushOutcome.Expired;

                case HttpStatusCode.RequestEntityTooLarge:
                    logger.LogError("Web Push service on {Host} refused a {Bytes}-byte body as too large",
                        endpoint.Host, body.Length);
                    return WebPushOutcome.TooLarge;

                case HttpStatusCode.TooManyRequests:
                    logger.LogWarning(
                        "Web Push service on {Host} is throttling us; Retry-After {RetryAfter}",
                        endpoint.Host, response.Headers.RetryAfter?.ToString() ?? "absent");
                    return WebPushOutcome.Throttled;

                default:
                    // 401/403 lands here, and it is the one worth reading a log for: it means the VAPID
                    // keypair or the subject is wrong, for every subscription at once rather than one.
                    logger.LogWarning("Web Push to {Host} answered {Status}",
                        endpoint.Host, (int)response.StatusCode);
                    return WebPushOutcome.Throttled;
            }
        }
    }

    /// <summary>A stable, short <c>Topic</c> for an arbitrary tag.</summary>
    public static string TopicOf(string tag) =>
        WebPushSubscription.Encode(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..22];
}
