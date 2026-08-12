using System.Net;
using AppEnvironment;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Push;

/// <summary>What the sender does with each answer a push service can give.</summary>
[TestFixture]
[Category("Unit")]
public class WebPushSenderTests
{
    [TearDown]
    public void ClearVapid() => StubWebPush.ResetEnv();

    [Test]
    public async Task A_created_response_is_a_delivery()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus, HttpStatusCode.Created);

        var outcome = await stub.Sender.SendAsync(stub.Subscription(), """{"type":"message"}""");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.Delivered));
        Assert.That(stub.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// The two statuses RFC 8030 gives this meaning, and the only two the sender may act on.
    /// </summary>
    [TestCase(HttpStatusCode.Gone)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task A_dead_subscription_is_reported_for_deletion(HttpStatusCode status)
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus, status);

        var outcome = await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.Expired));

        var published = bus.Published.OfType<PushEndpointExpiredEvent>().Single();
        Assert.That(published.Kind, Is.EqualTo(PushTokenKind.WebPush));
        Assert.That(published.Token, Is.EqualTo(StubWebPush.Endpoint));
    }

    /// <summary>Throttling must never delete a subscription.</summary>
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task A_transient_or_auth_failure_never_deletes_the_subscription(HttpStatusCode status)
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus, status);

        var outcome = await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.Throttled));
        Assert.That(bus.Published.OfType<PushEndpointExpiredEvent>(), Is.Empty);
    }

    [Test]
    public async Task A_payload_the_service_refuses_as_too_large_is_not_a_dead_subscription()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus, HttpStatusCode.RequestEntityTooLarge);

        var outcome = await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.TooLarge));
        Assert.That(bus.Published.OfType<PushEndpointExpiredEvent>(), Is.Empty);
    }

    /// <summary>
    /// An instance with no VAPID keypair has web push switched off by configuration.
    /// </summary>
    [Test]
    public async Task With_no_vapid_keypair_nothing_is_sent()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);
        var subscription = stub.Subscription();
        StubWebPush.ResetEnv();

        var outcome = await stub.Sender.SendAsync(subscription, "{}");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.NotAttempted));
        Assert.That(stub.Requests, Is.Empty);
    }

    /// <summary>A row written before the registration path validated keys cannot be encrypted to. It is
    /// skipped, not attempted with a null key.</summary>
    [Test]
    public async Task A_subscription_missing_its_keys_is_skipped()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);
        var subscription = stub.Subscription();
        subscription.P256dh = null;

        var outcome = await stub.Sender.SendAsync(subscription, "{}");

        Assert.That(outcome, Is.EqualTo(WebPushOutcome.NotAttempted));
        Assert.That(stub.Requests, Is.Empty);
    }

    /// <summary>Not an FCM token dressed up as a subscription.</summary>
    [Test]
    public void A_non_webpush_row_is_a_programming_error()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);
        var fcm = stub.Subscription();
        fcm.Kind = PushTokenKind.Fcm;

        Assert.ThrowsAsync<ArgumentException>(() => stub.Sender.SendAsync(fcm, "{}"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Headers - RFC 8030 §5, RFC 8188 §2, RFC 8292 §3
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>TTL</c> is required by RFC 8030 and some services answer 400 without it - which reads as a
    /// payload bug and sends you looking in the wrong place entirely.
    /// </summary>
    [Test]
    public async Task Every_request_carries_a_ttl()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);

        await stub.Sender.SendAsync(stub.Subscription(), "{}");

        var ttl = stub.Requests[0].Headers.GetValues("TTL").Single();
        Assert.That(int.Parse(ttl), Is.EqualTo((int)Env.Vapid.MessageTtl.TotalSeconds));
    }

    /// <summary>Without <c>Content-Encoding: aes128gcm</c> a receiver has no way to know how to read the
    /// body, and the browser reports a decryption failure rather than a bad header.</summary>
    [Test]
    public async Task Every_request_declares_the_content_encoding()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);

        await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(stub.Requests[0].Content!.Headers.ContentEncoding,
            Contains.Item(WebPushEncryption.ContentEncoding));
        Assert.That(stub.Requests[0].Content!.Headers.ContentType!.MediaType,
            Is.EqualTo("application/octet-stream"));
    }

    [Test]
    public async Task Every_request_is_vapid_authorised()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);

        await stub.Sender.SendAsync(stub.Subscription(), "{}");

        var authorization = stub.Requests[0].Headers.GetValues("Authorization").Single();
        Assert.That(authorization, Does.StartWith("vapid t="));
        Assert.That(authorization, Does.Contain(", k="));
    }

    [Test]
    public async Task The_request_is_a_post_to_the_endpoint()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);

        await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(stub.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(stub.Requests[0].RequestUri!.ToString(), Is.EqualTo(StubWebPush.Endpoint));
    }

    /// <summary>The body is the encrypted record, not the JSON.</summary>
    [Test]
    public async Task The_body_is_encrypted_and_not_the_plaintext()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);
        const string payload = """{"type":"message","body":"a recognisable secret"}""";

        await stub.Sender.SendAsync(stub.Subscription(), payload);

        var body = stub.Bodies[0];
        Assert.That(System.Text.Encoding.UTF8.GetString(body), Does.Not.Contain("recognisable secret"));
        Assert.That(body.Length, Is.EqualTo(16 + 4 + 1 + 65 + payload.Length + 1 + 16));
    }

    /// <summary>No topic, no header.</summary>
    [Test]
    public async Task Without_a_topic_no_topic_header_is_sent()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);

        await stub.Sender.SendAsync(stub.Subscription(), "{}");

        Assert.That(stub.Requests[0].Headers.Contains("Topic"), Is.False);
    }

    /// <summary>
    /// RFC 8030 §5.4 caps a Topic at 32 characters of base64url, and the client's tags are
    /// conversation ids that exceed that.
    /// </summary>
    [Test]
    public async Task A_topic_is_hashed_into_the_allowed_length()
    {
        var bus = new FakeMessageBus(_ => null!);
        var stub = StubWebPush.Create(bus);
        const string conversation = "conv_01JQZ0000000000000000000000-a-very-long-identifier";

        await stub.Sender.SendAsync(stub.Subscription(), "{}", topic: conversation);

        var topic = stub.Requests[0].Headers.GetValues("Topic").Single();
        Assert.That(topic, Has.Length.LessThanOrEqualTo(32));
        Assert.That(topic, Is.EqualTo(WebPushSender.TopicOf(conversation)));
        Assert.That(topic, Does.Match("^[A-Za-z0-9_-]+$"), "base64url only");
    }

    /// <summary>Same conversation, same topic - that is what makes it coalesce at the push service at
    /// all.</summary>
    [Test]
    public void The_same_tag_always_produces_the_same_topic()
    {
        Assert.That(WebPushSender.TopicOf("conv-1"), Is.EqualTo(WebPushSender.TopicOf("conv-1")));
        Assert.That(WebPushSender.TopicOf("conv-1"), Is.Not.EqualTo(WebPushSender.TopicOf("conv-2")));
    }

    /// <summary>Two ids sharing a long prefix must not share a topic.</summary>
    [Test]
    public void Topics_of_ids_sharing_a_prefix_differ()
    {
        var shared = new string('a', 40);

        Assert.That(WebPushSender.TopicOf(shared + "1"), Is.Not.EqualTo(WebPushSender.TopicOf(shared + "2")));
    }
}
