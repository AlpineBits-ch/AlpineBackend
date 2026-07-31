using System.Text;
using Messaging.Application.Services;

namespace Messaging.Tests.Services;

/// <summary>
/// Covers the data payload a message push carries, which is the contract two clients read directly
/// (MessagePushPayload.tryParse in Dart, NotificationService.swift on iOS).
///
/// The stakes are specific: this payload is what lets a device show the real text of an
/// end-to-end-encrypted message in the notification tray. Get the ciphertext encoding wrong and
/// every encrypted notification silently falls back to a placeholder; leave the plaintext in for an
/// encrypted message and the server has just leaked the thing MLS exists to protect.
///
/// Sending itself is not covered - it goes straight into the Firebase Admin SDK with no seam to
/// fake, the same accepted gap as CallPushService and PushNotifiaction elsewhere in this suite.
/// </summary>
[TestFixture]
public class MessagePushServiceTests
{
    private const string Ciphertext = "AAECAwQFBgcICQoLDA0ODw==";

    private static MessagePushPayload Encrypted(string? ciphertext = Ciphertext) => new()
    {
        MessageId = "msg_1",
        ContextId = "conv_1",
        ConversationId = "conv_1",
        AuthorId = "usr_sender",
        SenderName = "Ada",
        SenderAvatarUrl = "https://api.venta.gg/api/v1/social/profiles/p1/avatar",
        IsEncrypted = true,
        Content = Encoding.UTF8.GetBytes(ciphertext ?? string.Empty),
        MlsGeneration = 2,
    };

    private static MessagePushPayload Plain(string body) => new()
    {
        MessageId = "msg_2",
        ContextId = "chan_1",
        ChannelId = "chan_1",
        GuildId = "gld_1",
        AuthorId = "usr_sender",
        SenderName = "Ada",
        IsEncrypted = false,
        Content = Encoding.UTF8.GetBytes(body),
    };

    [Test]
    public void Encrypted_ShipsTheCiphertextAndThePlaceholder_NeverThePlaintext()
    {
        var data = MessagePushService.BuildData(Encrypted(), "usr_me");

        Assert.Multiple(() =>
        {
            Assert.That(data["encrypted"], Is.EqualTo("1"));
            Assert.That(data["ciphertext"], Is.EqualTo(Ciphertext));
            Assert.That(data["body"], Is.EqualTo(MessagePushService.EncryptedPlaceholder));
            Assert.That(data.ContainsKey("truncated"), Is.False);
        });
    }

    /// <summary>
    /// The sending client encrypts, base64s, and posts that string, which is stored as its UTF-8
    /// bytes. Base64-ing it a second time here would hand the device something openmls cannot
    /// parse - and the failure is silent, showing up only as "every encrypted notification is a
    /// placeholder".
    /// </summary>
    [Test]
    public void Encrypted_PassesTheStoredBase64Through_WithoutReEncodingIt()
    {
        var data = MessagePushService.BuildData(Encrypted(), "usr_me");

        Assert.That(Convert.FromBase64String(data["ciphertext"]), Has.Length.EqualTo(16));
    }

    [Test]
    public void Encrypted_CarriesTheGenerationTheCiphertextWasSealedUnder()
    {
        var data = MessagePushService.BuildData(Encrypted(), "usr_me");

        Assert.That(data["mlsGeneration"], Is.EqualTo("2"));
    }

    /// <summary>Encryption can be toggled off and on, and each stretch is a distinct group whose
    /// epochs restart at zero - a device with no generation cannot tell which group to try.</summary>
    [Test]
    public void Encrypted_OmitsTheGenerationRatherThanGuessingOne()
    {
        var payload = new MessagePushPayload
        {
            MessageId = "msg_1",
            ContextId = "conv_1",
            AuthorId = "usr_sender",
            SenderName = "Ada",
            IsEncrypted = true,
            Content = Encoding.UTF8.GetBytes(Ciphertext),
        };

        Assert.That(MessagePushService.BuildData(payload, "usr_me").ContainsKey("mlsGeneration"), Is.False);
    }

    /// <summary>FCM caps the data payload at 4KB. Dropping the ciphertext silently would be
    /// indistinguishable from a plaintext message, so the device is told which happened.</summary>
    [Test]
    public void Encrypted_TooLargeForFcm_DropsTheCiphertextAndSaysSo()
    {
        var data = MessagePushService.BuildData(Encrypted(new string('A', 5000)), "usr_me");

        Assert.Multiple(() =>
        {
            Assert.That(data.ContainsKey("ciphertext"), Is.False);
            Assert.That(data["truncated"], Is.EqualTo("1"));
            Assert.That(data["body"], Is.EqualTo(MessagePushService.EncryptedPlaceholder));
            Assert.That(data["senderName"], Is.EqualTo("Ada"));
        });
    }

    [Test]
    public void Plain_SendsTheRealBodyAndNoCiphertext()
    {
        var data = MessagePushService.BuildData(Plain("hello world"), "usr_me");

        Assert.Multiple(() =>
        {
            Assert.That(data["encrypted"], Is.EqualTo("0"));
            Assert.That(data["body"], Is.EqualTo("hello world"));
            Assert.That(data.ContainsKey("ciphertext"), Is.False);
            Assert.That(data.ContainsKey("truncated"), Is.False);
        });
    }

    /// <summary>MLS state is stored per user id on the device. Without this the extension and the
    /// background isolate have no directory to look in, whatever else the payload says.</summary>
    [Test]
    public void EveryPush_NamesTheAccountItIsFor()
    {
        Assert.That(MessagePushService.BuildData(Encrypted(), "usr_me")["recipientUserId"],
            Is.EqualTo("usr_me"));
    }

    [Test]
    public void ChannelPush_CarriesBothIdsSoTheTapCanBeRouted()
    {
        var data = MessagePushService.BuildData(Plain("hi"), "usr_me");

        Assert.Multiple(() =>
        {
            Assert.That(data["guildId"], Is.EqualTo("gld_1"));
            Assert.That(data["channelId"], Is.EqualTo("chan_1"));
            Assert.That(data.ContainsKey("conversationId"), Is.False);
        });
    }

    [Test]
    public void Push_CarriesTheSendersAvatarWhenThereIsOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessagePushService.BuildData(Encrypted(), "usr_me")["senderAvatarUrl"],
                Does.EndWith("/avatar"));
            Assert.That(MessagePushService.BuildData(Plain("hi"), "usr_me").ContainsKey("senderAvatarUrl"),
                Is.False);
        });
    }

    [Test]
    public void Push_IsTaggedSoTheClientCanTellItFromACallPush()
    {
        Assert.That(MessagePushService.BuildData(Encrypted(), "usr_me")["type"], Is.EqualTo("message"));
    }

    /// <summary>A notification shows two lines; the message length limit is far larger than that,
    /// and the whole payload has to fit in 4KB alongside the rest.</summary>
    [Test]
    public void Plain_TruncatesAnAbsurdlyLongBody()
    {
        var data = MessagePushService.BuildData(Plain(new string('x', 4000)), "usr_me");

        Assert.That(data["body"], Has.Length.EqualTo(500));
    }
}
