using System.Text;
using System.Text.Json;
using Messaging.Application.Services;

namespace Messaging.Tests.Push;

/// <summary>
/// The Web Push payload is the same object the FCM data payload is, serialised as JSON.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WebPushPayloadTests
{
    private const string Recipient = "user-2";

    private static MessagePushPayload Payload(
        bool encrypted = false,
        string content = "hello there",
        string? ciphertext = null,
        IReadOnlySet<string>? hideFor = null) => new()
    {
        MessageId = "msg-1",
        ContextId = "conv-1",
        ConversationId = "conv-1",
        AuthorId = "user-1",
        SenderName = "Ada",
        SenderAvatarUrl = "https://cdn.venta.test/a.png",
        IsEncrypted = encrypted,
        Content = Encoding.UTF8.GetBytes(ciphertext ?? content),
        MlsGeneration = encrypted ? 3 : null,
        HideContentForUserIds = hideFor ?? new HashSet<string>(StringComparer.Ordinal),
    };

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    /// <summary>Byte-for-byte the FCM data dictionary.</summary>
    [Test]
    public void The_payload_is_the_fcm_data_dictionary()
    {
        var payload = Payload();

        var web = Parse(MessagePushService.BuildWebPushPayload(
            payload, Recipient, WebPushEncryption.MaxPayloadBytes));

        Assert.That(web, Is.EqualTo(MessagePushService.BuildData(payload, Recipient)));
    }

    /// <summary>What the service worker draws the notification from.</summary>
    [Test]
    public void The_payload_carries_what_a_visible_notification_needs()
    {
        var web = Parse(MessagePushService.BuildWebPushPayload(
            Payload(), Recipient, WebPushEncryption.MaxPayloadBytes));

        Assert.That(web["senderName"], Is.EqualTo("Ada"));
        Assert.That(web["body"], Is.EqualTo("hello there"));
        Assert.That(web["conversationId"], Is.EqualTo("conv-1"));
    }

    /// <summary>T2-23 is inherited rather than reimplemented, which is the whole reason the payload is
    /// shared. A separate web shape would have had to remember to hide the sender's name.</summary>
    [Test]
    public void A_reader_who_hides_push_content_gets_the_placeholder_on_web_too()
    {
        var web = Parse(MessagePushService.BuildWebPushPayload(
            Payload(hideFor: new HashSet<string>([Recipient], StringComparer.Ordinal)),
            Recipient, WebPushEncryption.MaxPayloadBytes));

        Assert.That(web["hidden"], Is.EqualTo("1"));
        Assert.That(web.ContainsKey("senderName"), Is.False);
        Assert.That(web.ContainsKey("senderAvatarUrl"), Is.False);
        Assert.That(web.ContainsKey("ciphertext"), Is.False);
    }

    /// <summary>Always inside the encryption budget - the sender throws above it, and a throw on this
    /// path would be one recipient losing a notification for a reason nothing surfaces.</summary>
    [Test]
    public void The_payload_always_fits_the_encryption_budget()
    {
        var big = new string('x', 5000);

        var json = MessagePushService.BuildWebPushPayload(
            Payload(content: big), Recipient, WebPushEncryption.MaxPayloadBytes);

        Assert.That(Encoding.UTF8.GetByteCount(json),
            Is.LessThanOrEqualTo(WebPushEncryption.MaxPayloadBytes));
    }

    /// <summary>
    /// The ciphertext is the only unbounded field, so it is what gets dropped - and dropping it is
    /// announced.
    /// </summary>
    [Test]
    public void An_oversized_ciphertext_is_dropped_and_announced()
    {
        var payload = Payload(encrypted: true, ciphertext: new string('A', 2900));

        var web = Parse(MessagePushService.BuildWebPushPayload(payload, Recipient, 1024));

        Assert.That(web.ContainsKey("ciphertext"), Is.False);
        Assert.That(web["truncated"], Is.EqualTo("1"));
    }

    /// <summary>A ciphertext that fits is kept: a client that can decrypt shows the real message rather
    /// than the placeholder, which is the entire reason it travels.</summary>
    [Test]
    public void A_ciphertext_that_fits_is_kept()
    {
        var payload = Payload(encrypted: true, ciphertext: "c2hvcnQ=");

        var web = Parse(MessagePushService.BuildWebPushPayload(
            payload, Recipient, WebPushEncryption.MaxPayloadBytes));

        Assert.That(web["ciphertext"], Is.EqualTo("c2hvcnQ="));
        Assert.That(web["mlsGeneration"], Is.EqualTo("3"));
    }

    /// <summary>Routing ids survive even the tightest budget.</summary>
    [Test]
    public void Routing_ids_survive_a_budget_nothing_else_fits_in()
    {
        var payload = Payload(content: new string('x', 4000));

        var web = Parse(MessagePushService.BuildWebPushPayload(payload, Recipient, 320));

        Assert.That(web["messageId"], Is.EqualTo("msg-1"));
        Assert.That(web["contextId"], Is.EqualTo("conv-1"));
        Assert.That(web["conversationId"], Is.EqualTo("conv-1"));
        Assert.That(web["recipientUserId"], Is.EqualTo(Recipient));
    }
}
